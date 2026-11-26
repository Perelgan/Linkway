package main

import (
	"crypto/sha256"
	"errors"
	"fmt"
	"os"
	"path/filepath"
	"regexp"
	"runtime"
	"strconv"
	"strings"
	"syscall"
	"unsafe"
)

var stagePattern = regexp.MustCompile(`^\.vpn-update-[0-9a-f]{32}$`)
var payloadItems = []string{"Libraries", "Languages", "Licenses", "README.md"}

func lockInstallation(root string) (func(), error) {
	kernel := syscall.NewLazyDLL("kernel32.dll")
	createMutex := kernel.NewProc("CreateMutexW")
	releaseMutex := kernel.NewProc("ReleaseMutex")
	hash := sha256.Sum256([]byte(strings.ToLower(filepath.Clean(root))))
	name, _ := syscall.UTF16PtrFromString(fmt.Sprintf(`Local\VPNDesktop-Update-%x`, hash))

	runtime.LockOSThread()
	raw, _, callErr := createMutex.Call(0, 0, uintptr(unsafe.Pointer(name)))
	if raw == 0 {
		runtime.UnlockOSThread()
		return nil, callErr
	}
	handle := syscall.Handle(raw)
	state, err := syscall.WaitForSingleObject(handle, 0)
	if err != nil || (state != syscall.WAIT_OBJECT_0 && state != syscall.WAIT_ABANDONED) {
		syscall.CloseHandle(handle)
		runtime.UnlockOSThread()
		if err != nil {
			return nil, err
		}
		return nil, errors.New("Установка обновления уже идёт. Подождите и откройте приложение ещё раз.")
	}
	return func() {
		releaseMutex.Call(raw)
		syscall.CloseHandle(handle)
		runtime.UnlockOSThread()
	}, nil
}

func installUpdate(pidArgument string) error {
	executable, err := os.Executable()
	if err != nil {
		return err
	}
	stage := filepath.Dir(executable)
	root := filepath.Dir(stage)
	if filepath.Base(executable) != "updater.exe" || !stagePattern.MatchString(filepath.Base(stage)) || !isPlainDirectory(stage) {
		return errors.New("Не удалось проверить папку обновления.")
	}
	unlock, err := lockInstallation(root)
	if err != nil {
		return err
	}
	defer unlock()

	if err := waitForApplication(pidArgument); err != nil {
		return err
	}
	payload := filepath.Join(stage, "package", "VPNDesktop")
	backup := filepath.Join(stage, "backup")
	if !isPlainDirectory(filepath.Join(stage, "package")) || !isPlainDirectory(payload) {
		return errors.New("Не удалось найти распакованную сборку обновления.")
	}
	items := append(append([]string{}, payloadItems...), "VPNDesktop.exe")
	for _, name := range items {
		if err := validatePayloadItem(root, name); err != nil {
			return err
		}
		if err := validatePayloadItem(payload, name); err != nil {
			return err
		}
	}
	if err := os.Mkdir(backup, 0700); err != nil {
		return err
	}

	err = installPayload(root, payload, backup)
	if err != nil {
		if rollbackErr := restoreBackup(root, stage); rollbackErr != nil {
			return fmt.Errorf("%w\nНе удалось полностью восстановить прежнюю сборку: %v\nРезервная копия: %s", err, rollbackErr, backup)
		}
		return err
	}
	return nil
}

func waitForApplication(pidArgument string) error {
	pid, err := strconv.ParseUint(pidArgument, 10, 32)
	if err != nil || pid == 0 {
		return errors.New("Не удалось определить процесс клиента.")
	}
	handle, err := syscall.OpenProcess(syscall.SYNCHRONIZE, false, uint32(pid))
	if errors.Is(err, syscall.Errno(87)) { // ERROR_INVALID_PARAMETER: process already exited.
		return nil
	}
	if err != nil {
		return err
	}
	defer syscall.CloseHandle(handle)
	state, err := syscall.WaitForSingleObject(handle, 45000)
	if err != nil {
		return err
	}
	if state != syscall.WAIT_OBJECT_0 {
		return errors.New("Клиент не закрылся вовремя. Запустите обновление ещё раз.")
	}
	return nil
}

func installPayload(root, payload, backup string) error {
	for _, name := range payloadItems {
		target := filepath.Join(root, name)
		if err := os.Rename(target, filepath.Join(backup, name)); err != nil {
			return err
		}
		if err := os.Rename(filepath.Join(payload, name), target); err != nil {
			return err
		}
	}

	// The launcher is swapped last. Its absence from the payload marks a completed transaction.
	return replaceLauncher(filepath.Join(payload, "VPNDesktop.exe"), filepath.Join(root, "VPNDesktop.exe"))
}

func replaceLauncher(source, destination string) error {
	moveFile := syscall.NewLazyDLL("kernel32.dll").NewProc("MoveFileExW")
	from, err := syscall.UTF16PtrFromString(source)
	if err != nil {
		return err
	}
	to, err := syscall.UTF16PtrFromString(destination)
	if err != nil {
		return err
	}
	const replaceAndFlush = 0x1 | 0x8
	ok, _, callErr := moveFile.Call(uintptr(unsafe.Pointer(from)), uintptr(unsafe.Pointer(to)), replaceAndFlush)
	if ok == 0 {
		return callErr
	}
	return nil
}

func recoverUpdates(root string) error {
	entries, err := os.ReadDir(root)
	if err != nil {
		return err
	}
	for _, entry := range entries {
		stage := filepath.Join(root, entry.Name())
		if !stagePattern.MatchString(entry.Name()) || !isPlainDirectory(stage) ||
			!isPlainDirectory(filepath.Join(stage, "backup")) {
			continue
		}
		_, err := os.Lstat(filepath.Join(stage, "package", "VPNDesktop", "VPNDesktop.exe"))
		switch {
		case err == nil:
			if err := restoreBackup(root, stage); err != nil {
				return fmt.Errorf("Не удалось восстановить прежнюю сборку из %s: %w", stage, err)
			}
		case !errors.Is(err, os.ErrNotExist):
			return err
		}
		// The helper may still be exiting. A later launch can repeat the cleanup.
		_ = os.RemoveAll(stage)
	}
	return nil
}

func restoreBackup(root, stage string) error {
	discard := filepath.Join(stage, "discard")
	if err := os.Mkdir(discard, 0700); err != nil && !errors.Is(err, os.ErrExist) {
		return err
	}
	if !isPlainDirectory(discard) {
		return errors.New("Недопустимый путь восстановления.")
	}
	for i := len(payloadItems) - 1; i >= 0; i-- {
		name := payloadItems[i]
		saved := filepath.Join(stage, "backup", name)
		if _, err := os.Lstat(saved); errors.Is(err, os.ErrNotExist) {
			continue
		} else if err != nil {
			return err
		}
		target := filepath.Join(root, name)
		if _, err := os.Lstat(target); err == nil {
			if err := os.Rename(target, filepath.Join(discard, name)); err != nil {
				return err
			}
		} else if !errors.Is(err, os.ErrNotExist) {
			return err
		}
		if err := os.Rename(saved, target); err != nil {
			return err
		}
	}
	return nil
}

func validatePayloadItem(parent, name string) error {
	path := filepath.Join(parent, name)
	attributes, err := attributesOf(path)
	wantDirectory := name == "Libraries" || name == "Languages" || name == "Licenses"
	if err != nil || attributes&syscall.FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
		(attributes&syscall.FILE_ATTRIBUTE_DIRECTORY != 0) != wantDirectory {
		return fmt.Errorf("Не удалось проверить %s", path)
	}
	return nil
}

func isPlainDirectory(path string) bool {
	attributes, err := attributesOf(path)
	return err == nil && attributes&syscall.FILE_ATTRIBUTE_DIRECTORY != 0 && attributes&syscall.FILE_ATTRIBUTE_REPARSE_POINT == 0
}

func attributesOf(path string) (uint32, error) {
	text, err := syscall.UTF16PtrFromString(path)
	if err != nil {
		return 0, err
	}
	return syscall.GetFileAttributes(text)
}

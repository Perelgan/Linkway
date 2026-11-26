package main

import (
	"os"
	"os/exec"
	"path/filepath"
	"syscall"
	"unsafe"
)

func main() {
	if len(os.Args) == 3 && os.Args[1] == "--install-update" {
		installed, err := installUpdate(os.Args[2])
		if err != nil {
			message := "Не удалось установить обновление Linkway."
			if installed {
				message = "Обновление Linkway установлено. Откройте VPNDesktop.exe вручную."
			}
			showError(message + "\n\n" + err.Error())
			os.Exit(1)
		}
		return
	}
	if err := startApplication(); err != nil {
		showError("Не удалось запустить Linkway. Убедитесь, что папка Libraries находится рядом с VPNDesktop.exe.\n\n" + err.Error())
		os.Exit(1)
	}
}

func startApplication() error {
	executable, err := os.Executable()
	if err != nil {
		return err
	}

	root := filepath.Dir(executable)
	unlock, err := lockInstallation(root)
	if err != nil {
		return err
	}
	defer unlock()
	if err := recoverUpdates(root); err != nil {
		return err
	}

	return launchApplication(root, os.Args[1:])
}

func launchApplication(root string, args []string) error {
	libraries := filepath.Join(root, "Libraries")
	command := exec.Command(filepath.Join(libraries, "VPNDesktop.App.exe"), args...)
	command.Dir = libraries
	if err := command.Start(); err != nil {
		return err
	}
	return command.Process.Release()
}

func showError(message string) {
	messageBox := syscall.NewLazyDLL("user32.dll").NewProc("MessageBoxW")
	title, _ := syscall.UTF16PtrFromString("Linkway")
	body, _ := syscall.UTF16PtrFromString(message)
	messageBox.Call(0, uintptr(unsafe.Pointer(body)), uintptr(unsafe.Pointer(title)), 0x10)
}

package main

import (
	"crypto/sha256"
	"encoding/hex"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"io/fs"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"syscall"
)

const (
	launcherName      = "VPNDesktop.exe"
	manifestName      = "update-files.json"
	journalName       = "file-transaction.json"
	maxUpdateMetadata = 2 << 20
	maxManagedFiles   = 8192
)

type fileChange struct {
	Path           string
	HadOriginal    bool
	HasReplacement bool
}

type fileTransaction struct {
	Changes         []fileChange
	LauncherChanged bool
	Committed       bool
}

type fileManifest struct {
	Files []manifestEntry
}

type manifestEntry struct {
	Path   string
	Size   int64
	Sha256 string
}

func installFiles(root, stage string) error {
	payload := filepath.Join(stage, "package", "VPNDesktop")
	if !isPlainDirectory(filepath.Dir(payload)) || !isPlainDirectory(payload) {
		return errors.New("Не удалось найти распакованную сборку обновления.")
	}
	for _, name := range append(append([]string{}, payloadItems...), launcherName) {
		if err := validatePayloadItem(root, name); err != nil {
			return err
		}
	}
	plan, err := planFiles(root, payload, stage)
	if err != nil {
		return err
	}
	if err := saveTransaction(stage, plan); err != nil {
		return err
	}
	if err := applyFileUpdate(root, payload, stage, plan); err != nil {
		if rollbackErr := rollbackFileUpdate(root, payload, stage, plan); rollbackErr != nil {
			return fmt.Errorf("%w\nНе удалось восстановить прежнюю сборку: %v\nРезервная копия: %s", err, rollbackErr, stage)
		}
		_ = os.RemoveAll(stage)
		return err
	}
	return nil
}

func planFiles(root, payload, stage string) (fileTransaction, error) {
	installed, err := collectManagedFiles(root)
	if err != nil {
		return fileTransaction{}, err
	}
	replacements, err := collectManagedFiles(payload)
	if err != nil {
		return fileTransaction{}, err
	}

	var manifest fileManifest
	partial, err := readMetadata(stage, manifestName, &manifest)
	if err != nil {
		return fileTransaction{}, err
	}
	target := replacements
	if partial {
		target = make(map[string]string, len(manifest.Files))
		for _, entry := range manifest.Files {
			name := filepath.FromSlash(entry.Path)
			key := strings.ToLower(name)
			if !managedFile(name, true) || target[key] != "" || entry.Size < 0 || len(entry.Sha256) != 64 {
				return fileTransaction{}, errors.New("Некорректный список файлов обновления.")
			}
			parent, source := root, installed[key]
			if replacement := replacements[key]; replacement != "" {
				parent, source = payload, replacement
			}
			if source == "" {
				return fileTransaction{}, fmt.Errorf("Не найден файл обновления: %s", name)
			}
			if err := checkManifestFile(filepath.Join(parent, source), entry); err != nil {
				return fileTransaction{}, fmt.Errorf("Файл не совпадает с обновлением: %s: %w", name, err)
			}
			target[key] = name
		}
		for key := range replacements {
			if target[key] == "" {
				return fileTransaction{}, errors.New("Обнаружен файл вне списка обновления.")
			}
		}
	}
	for _, name := range []string{launcherName, "README.md", filepath.Join("Libraries", "VPNDesktop.App.exe"),
		filepath.Join("Libraries", "VPNDesktop.dll"), filepath.Join("Libraries", "mihomo.exe"), filepath.Join("Libraries", "wintun.dll")} {
		if target[strings.ToLower(name)] == "" {
			return fileTransaction{}, errors.New("Список файлов обновления неполный.")
		}
	}

	keys := make([]string, 0, len(installed)+len(target))
	for key := range installed {
		keys = append(keys, key)
	}
	for key := range target {
		if installed[key] == "" {
			keys = append(keys, key)
		}
	}
	sort.Strings(keys)
	var plan fileTransaction
	for _, key := range keys {
		old, updated, replacement := installed[key], target[key], replacements[key]
		if updated != "" && replacement == "" {
			continue
		} // verified unchanged in the partial archive
		if old != "" && replacement != "" {
			same, err := sameFileContents(filepath.Join(root, old), filepath.Join(payload, replacement))
			if err != nil {
				return fileTransaction{}, err
			}
			if same {
				continue
			}
		}
		if key == strings.ToLower(launcherName) {
			plan.LauncherChanged = true
			continue
		}
		name := old
		if replacement != "" {
			name = replacement
		}
		if _, _, err := updatePath(root, name); err != nil {
			return fileTransaction{}, err
		}
		plan.Changes = append(plan.Changes, fileChange{name, old != "", replacement != ""})
	}
	return plan, nil
}

func collectManagedFiles(root string) (map[string]string, error) {
	files := make(map[string]string)
	for _, item := range append(append([]string{}, payloadItems...), launcherName) {
		path := filepath.Join(root, item)
		if _, err := os.Lstat(path); errors.Is(err, os.ErrNotExist) {
			continue
		} else if err != nil {
			return nil, err
		}
		err := filepath.WalkDir(path, func(path string, entry fs.DirEntry, err error) error {
			if err != nil {
				return err
			}
			flags, err := attributesOf(path)
			if err != nil {
				return err
			}
			if flags&syscall.FILE_ATTRIBUTE_REPARSE_POINT != 0 {
				return fmt.Errorf("Обновление не обрабатывает ссылки: %s", path)
			}
			if entry.IsDir() {
				return nil
			}
			relative, err := filepath.Rel(root, path)
			if err != nil {
				return err
			}
			key := strings.ToLower(relative)
			if !managedFile(relative, true) || !entry.Type().IsRegular() || files[key] != "" || len(files) == maxManagedFiles {
				return fmt.Errorf("Некорректное дерево файлов обновления: %s", path)
			}
			files[key] = relative
			return nil
		})
		if err != nil {
			return nil, err
		}
	}
	return files, nil
}

func managedFile(name string, allowLauncher bool) bool {
	if !filepath.IsLocal(name) || filepath.Clean(name) != name || strings.ContainsAny(name, "/:") {
		return false
	}
	for _, part := range strings.Split(name, string(filepath.Separator)) {
		if strings.TrimRight(part, " .") != part || !filepath.IsLocal(part) {
			return false
		}
	}
	first, rest, nested := strings.Cut(strings.ToLower(name), string(filepath.Separator))
	if !nested {
		return first == "readme.md" || allowLauncher && first == "vpndesktop.exe"
	}
	return rest != "" && (first == "libraries" || first == "languages" || first == "licenses")
}

func updatePath(parent, name string) (string, bool, error) {
	result := filepath.Join(parent, name)
	if !managedFile(name, true) || !isPlainDirectory(parent) {
		return "", false, errors.New("Недопустимый путь обновления.")
	}
	parts := strings.Split(name, string(filepath.Separator))
	current := parent
	for index, part := range parts {
		current = filepath.Join(current, part)
		flags, err := attributesOf(current)
		if errors.Is(err, os.ErrNotExist) {
			return result, false, nil
		}
		if err != nil {
			return "", false, err
		}
		if flags&syscall.FILE_ATTRIBUTE_REPARSE_POINT != 0 ||
			(flags&syscall.FILE_ATTRIBUTE_DIRECTORY != 0) != (index < len(parts)-1) {
			return "", false, fmt.Errorf("Конфликт пути обновления: %s", name)
		}
	}
	return result, true, nil
}

func fileDigest(path string) (string, error) {
	input, err := os.Open(path)
	if err != nil {
		return "", err
	}
	defer input.Close()
	hash := sha256.New()
	if _, err := io.Copy(hash, input); err != nil {
		return "", err
	}
	return hex.EncodeToString(hash.Sum(nil)), nil
}

func checkManifestFile(path string, entry manifestEntry) error {
	info, err := os.Stat(path)
	if err != nil {
		return err
	}
	if info.Size() != entry.Size {
		return errors.New("размер отличается")
	}
	digest, err := fileDigest(path)
	if err != nil {
		return err
	}
	if !strings.EqualFold(digest, entry.Sha256) {
		return errors.New("контрольная сумма отличается")
	}
	return nil
}

func sameFileContents(left, right string) (bool, error) {
	a, err := os.Stat(left)
	if err != nil {
		return false, err
	}
	b, err := os.Stat(right)
	if err != nil {
		return false, err
	}
	if a.Size() != b.Size() {
		return false, nil
	}
	leftDigest, err := fileDigest(left)
	if err != nil {
		return false, err
	}
	rightDigest, err := fileDigest(right)
	return leftDigest == rightDigest, err
}

func applyFileUpdate(root, payload, stage string, plan fileTransaction) error {
	backup := filepath.Join(stage, "file-backup")
	if err := os.Mkdir(backup, 0700); err != nil {
		return err
	}
	for _, change := range plan.Changes {
		target, exists, err := updatePath(root, change.Path)
		if err != nil {
			return err
		}
		if exists != change.HadOriginal {
			return errors.New("Файлы установки изменились во время обновления.")
		}
		if change.HadOriginal {
			saved, _, err := updatePath(backup, change.Path)
			if err != nil {
				return err
			}
			if err := os.MkdirAll(filepath.Dir(saved), 0700); err != nil {
				return err
			}
			if err := moveFile(target, saved); err != nil {
				return err
			}
		}
		if change.HasReplacement {
			candidate, present, err := updatePath(payload, change.Path)
			if err != nil {
				return err
			}
			if !present {
				return errors.New("Отсутствует файл в распакованном обновлении.")
			}
			if err := os.MkdirAll(filepath.Dir(target), 0700); err != nil {
				return err
			}
			if err := moveFile(candidate, target); err != nil {
				return err
			}
		}
	}
	if plan.LauncherChanged {
		// Keep the old launcher in place until there is nothing left to roll back.
		return moveFile(filepath.Join(payload, launcherName), filepath.Join(root, launcherName))
	}
	plan.Committed = true
	return saveTransaction(stage, plan)
}

func rollbackFileUpdate(root, payload, stage string, plan fileTransaction) error {
	backup := filepath.Join(stage, "file-backup")
	if !isPlainDirectory(backup) {
		if _, err := os.Lstat(backup); errors.Is(err, os.ErrNotExist) {
			return nil
		}
		return errors.New("Недопустимая резервная копия обновления.")
	}
	for index := len(plan.Changes) - 1; index >= 0; index-- {
		change := plan.Changes[index]
		target, exists, err := updatePath(root, change.Path)
		if err != nil {
			return err
		}
		if change.HadOriginal {
			saved, present, err := updatePath(backup, change.Path)
			if err != nil {
				return err
			}
			if present {
				if err := moveFile(saved, target); err != nil {
					return err
				}
			}
		} else if exists {
			candidate, present, err := updatePath(payload, change.Path)
			if err != nil {
				return err
			}
			if !present {
				if err := moveFile(target, candidate); err != nil {
					return err
				}
			}
		}
	}
	return nil
}

func saveTransaction(stage string, plan fileTransaction) error {
	if len(plan.Changes) > maxManagedFiles {
		return errors.New("Недопустимое число операций обновления.")
	}
	data, err := json.Marshal(plan)
	if err != nil {
		return err
	}
	if len(data) > maxUpdateMetadata {
		return errors.New("Журнал обновления слишком большой.")
	}
	journal := filepath.Join(stage, journalName)
	temp, err := os.CreateTemp(stage, "journal-*")
	if err != nil {
		return err
	}
	defer os.Remove(temp.Name())
	_, err = temp.Write(data)
	if err == nil {
		err = temp.Sync()
	}
	closeErr := temp.Close()
	if err != nil {
		return err
	}
	if closeErr != nil {
		return closeErr
	}
	return moveFile(temp.Name(), journal)
}

func readMetadata(stage, name string, result any) (bool, error) {
	path := filepath.Join(stage, name)
	flags, err := attributesOf(path)
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	if flags&(syscall.FILE_ATTRIBUTE_DIRECTORY|syscall.FILE_ATTRIBUTE_REPARSE_POINT) != 0 {
		return false, errors.New("Недопустимые метаданные обновления.")
	}
	input, err := os.Open(path)
	if err != nil {
		return false, err
	}
	defer input.Close()
	data, err := io.ReadAll(io.LimitReader(input, maxUpdateMetadata+1))
	if err != nil {
		return false, err
	}
	if len(data) > maxUpdateMetadata {
		return false, errors.New("Метаданные обновления слишком большие.")
	}
	return true, json.Unmarshal(data, result)
}

func recoverFileUpdate(root, stage string) (bool, error) {
	var plan fileTransaction
	found, err := readMetadata(stage, journalName, &plan)
	if err != nil || !found {
		return found, err
	}
	if len(plan.Changes) > maxManagedFiles {
		return true, errors.New("Недопустимое число операций восстановления.")
	}
	seen := make(map[string]bool, len(plan.Changes))
	for _, change := range plan.Changes {
		key := strings.ToLower(change.Path)
		if !managedFile(change.Path, false) || seen[key] || (!change.HadOriginal && !change.HasReplacement) {
			return true, errors.New("Недопустимое имя в журнале восстановления.")
		}
		seen[key] = true
	}
	if plan.Committed {
		return true, nil
	}
	payload := filepath.Join(stage, "package", "VPNDesktop")
	if plan.LauncherChanged {
		if _, err := os.Lstat(filepath.Join(payload, launcherName)); errors.Is(err, os.ErrNotExist) {
			// An atomic launcher replacement completed the transaction.
			return true, nil
		} else if err != nil {
			return true, err
		}
	}
	if !isPlainDirectory(filepath.Dir(payload)) || !isPlainDirectory(payload) {
		return true, errors.New("Недопустимая папка обновления при восстановлении.")
	}
	return true, rollbackFileUpdate(root, payload, stage, plan)
}

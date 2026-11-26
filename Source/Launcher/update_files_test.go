package main

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"
)

const fixtureDll = "Libraries\\VPNDesktop.dll"

func writeFixture(t *testing.T, root, name, value string) {
	t.Helper()
	path := filepath.Join(root, name)
	if err := os.MkdirAll(filepath.Dir(path), 0700); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(path, []byte(value), 0600); err != nil {
		t.Fatal(err)
	}
}

func readFixture(t *testing.T, root, name string) string {
	t.Helper()
	data, err := os.ReadFile(filepath.Join(root, name))
	if err != nil {
		t.Fatal(err)
	}
	return string(data)
}

func fixtureTrees(t *testing.T) (string, string, string) {
	root := t.TempDir()
	stage := filepath.Join(root, ".vpn-update-00000000000000000000000000000001")
	payload := filepath.Join(stage, "package", "VPNDesktop")
	for _, dir := range []string{"Libraries", "Languages", "Licenses"} {
		if err := os.MkdirAll(filepath.Join(root, dir), 0700); err != nil {
			t.Fatal(err)
		}
	}
	for _, name := range []string{launcherName, "README.md", fixtureDll, "Libraries\\VPNDesktop.App.exe",
		"Libraries\\mihomo.exe", "Libraries\\wintun.dll", "Libraries\\coreclr.dll"} {
		writeFixture(t, root, name, "same "+name)
		writeFixture(t, payload, name, "same "+name)
	}
	return root, stage, payload
}

func TestUpdateLeavesUnchangedFilesInPlace(t *testing.T) {
	root, stage, payload := fixtureTrees(t)
	writeFixture(t, root, "Libraries\\obsolete.dll", "old")
	writeFixture(t, root, "user.txt", "user")
	writeFixture(t, payload, fixtureDll, "new")
	writeFixture(t, payload, "Languages\\ru\\new.dll", "added")
	untouched := filepath.Join(root, "Libraries", "coreclr.dll")
	oldTime := time.Date(2020, 1, 1, 0, 0, 0, 0, time.UTC)
	if err := os.Chtimes(untouched, oldTime, oldTime); err != nil {
		t.Fatal(err)
	}

	if err := installFiles(root, stage); err != nil {
		t.Fatal(err)
	}
	for name, want := range map[string]string{fixtureDll: "new", "Languages\\ru\\new.dll": "added", "user.txt": "user"} {
		if got := readFixture(t, root, name); got != want {
			t.Fatalf("%s = %q, want %q", name, got, want)
		}
	}
	if _, err := os.Stat(filepath.Join(root, "Libraries", "obsolete.dll")); !os.IsNotExist(err) {
		t.Fatalf("obsolete file remains: %v", err)
	}
	current, err := os.Stat(untouched)
	if err != nil || !current.ModTime().Equal(oldTime) {
		t.Fatalf("unchanged file was replaced: %v", err)
	}
	if ok, err := recoverFileUpdate(root, stage); !ok || err != nil {
		t.Fatalf("recover committed update: %v %v", ok, err)
	}
	if readFixture(t, root, fixtureDll) != "new" {
		t.Fatal("committed update rolled back")
	}
}

func TestPartialUpdateVerifiesFilesItKeeps(t *testing.T) {
	root, stage, payload := fixtureTrees(t)
	writeFixture(t, payload, fixtureDll, "new")
	files, err := collectManagedFiles(payload)
	if err != nil {
		t.Fatal(err)
	}
	var manifest fileManifest
	for _, name := range files {
		from := filepath.Join(payload, name)
		info, err := os.Stat(from)
		if err != nil {
			t.Fatal(err)
		}
		digest, err := fileDigest(from)
		if err != nil {
			t.Fatal(err)
		}
		manifest.Files = append(manifest.Files, manifestEntry{filepath.ToSlash(name), info.Size(), digest})
		if name != fixtureDll {
			if err := os.Remove(from); err != nil {
				t.Fatal(err)
			}
		}
	}
	data, err := json.Marshal(manifest)
	if err != nil {
		t.Fatal(err)
	}
	writeFixture(t, stage, manifestName, string(data))
	writeFixture(t, root, "Libraries\\coreclr.dll", "changed locally")
	if err := installFiles(root, stage); err == nil {
		t.Fatal("update accepted modified unchanged file")
	}
	if got := readFixture(t, root, fixtureDll); got != "same "+fixtureDll {
		t.Fatal("rejected update touched the installation")
	}
	writeFixture(t, root, "Libraries\\coreclr.dll", "same Libraries\\coreclr.dll")
	if err := installFiles(root, stage); err != nil {
		t.Fatal(err)
	}
	if readFixture(t, root, fixtureDll) != "new" {
		t.Fatal("partial update did not install the replacement")
	}
}

func TestInterruptedUpdateRestoresFiles(t *testing.T) {
	root, stage, payload := fixtureTrees(t)
	writeFixture(t, payload, fixtureDll, "new")
	writeFixture(t, payload, "Languages\\ru\\added.dll", "added")
	plan, err := planFiles(root, payload, stage)
	if err != nil {
		t.Fatal(err)
	}
	if err := saveTransaction(stage, plan); err != nil {
		t.Fatal(err)
	}
	actual := plan
	actual.Changes = append(append([]fileChange{}, plan.Changes...), fileChange{"Libraries\\missing.dll", false, true})
	if err := applyFileUpdate(root, payload, stage, actual); err == nil {
		t.Fatal("expected a simulated interruption")
	}
	if got := readFixture(t, root, fixtureDll); got != "new" {
		t.Fatal("fixture did not reach a partial installation")
	}
	for attempt := 0; attempt < 2; attempt++ {
		if ok, err := recoverFileUpdate(root, stage); !ok || err != nil {
			t.Fatalf("recover interrupted update: %v %v", ok, err)
		}
	}
	if got := readFixture(t, root, fixtureDll); got != "same "+fixtureDll {
		t.Fatal("previous DLL was not restored")
	}
	if _, err := os.Stat(filepath.Join(root, "Languages", "ru", "added.dll")); !os.IsNotExist(err) {
		t.Fatalf("new file remains: %v", err)
	}
}

func TestMain(m *testing.M) {
	if marker := os.Getenv("LINKWAY_TEST_RESTART_MARKER"); marker != "" {
		dir, err := os.Getwd()
		if err != nil || os.WriteFile(marker, []byte(dir), 0600) != nil {
			os.Exit(1)
		}
		os.Exit(0)
	}
	os.Exit(m.Run())
}

func TestUpdatedApplicationLaunches(t *testing.T) {
	root, stage, payload := fixtureTrees(t)
	executable, err := os.Executable()
	if err != nil {
		t.Fatal(err)
	}
	fixture, err := os.ReadFile(executable)
	if err != nil {
		t.Fatal(err)
	}
	app := filepath.Join("Libraries", "VPNDesktop.App.exe")
	if err := os.WriteFile(filepath.Join(payload, app), fixture, 0700); err != nil {
		t.Fatal(err)
	}
	marker := filepath.Join(root, "restarted")
	t.Setenv("LINKWAY_TEST_RESTART_MARKER", marker)
	if err := installFiles(root, stage); err != nil {
		t.Fatal(err)
	}
	if err := launchApplication(root, nil); err != nil {
		t.Fatal(err)
	}
	deadline := time.Now().Add(5 * time.Second)
	for time.Now().Before(deadline) {
		value, err := os.ReadFile(marker)
		if err == nil && filepath.Clean(string(value)) == filepath.Join(root, "Libraries") {
			return
		}
		time.Sleep(25 * time.Millisecond)
	}
	t.Fatal("the installed application did not start in its library directory")
}

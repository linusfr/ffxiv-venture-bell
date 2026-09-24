package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io/fs"
	"os"
	"path/filepath"
)

// Store persists state as one JSON file. A database would be three orders of
// magnitude more machinery than ten rows of retainer timers deserve.
type Store struct {
	path string
}

func NewStore(path string) *Store { return &Store{path: path} }

func (s *Store) Load() (*State, error) {
	b, err := os.ReadFile(s.path)
	if errors.Is(err, fs.ErrNotExist) {
		return NewState(), nil
	}
	if err != nil {
		return nil, err
	}

	var st State
	if err := json.Unmarshal(b, &st); err != nil {
		return nil, fmt.Errorf("%s is not readable state: %w", s.path, err)
	}
	if st.Characters == nil {
		st.Characters = map[string]*character{}
	}
	return &st, nil
}

// Save writes through a temporary file in the same directory. A half-written
// state file would fail to parse on the next boot and take the timers with it.
func (s *Store) Save(st *State) error {
	b, err := json.MarshalIndent(st, "", "  ")
	if err != nil {
		return err
	}

	// Created on every save rather than at startup: the path is user-supplied,
	// and a directory that disappears under a running server should not cost
	// every pending timer.
	dir := filepath.Dir(s.path)
	if err := os.MkdirAll(dir, 0o700); err != nil {
		return err
	}

	tmp, err := os.CreateTemp(dir, ".state-*.json")
	if err != nil {
		return err
	}
	defer os.Remove(tmp.Name())

	if err := tmp.Chmod(0o600); err != nil {
		tmp.Close()
		return err
	}
	if _, err := tmp.Write(b); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Sync(); err != nil {
		tmp.Close()
		return err
	}
	if err := tmp.Close(); err != nil {
		return err
	}
	return os.Rename(tmp.Name(), s.path)
}

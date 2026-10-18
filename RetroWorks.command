#!/bin/bash
# RetroWorks ][ launcher for macOS - double-click to start.
cd "$(dirname "$0")" || exit 1
printf '\033]0;RetroWorks ][\007'      # window title
printf '\033[8;24;80t'                 # resize the window to 80 x 24
if ! /usr/bin/python3 -c 'import curses' >/dev/null 2>&1; then
    echo "RetroWorks needs Python 3."
    echo "If macOS offers to install the Command Line Tools, click Install,"
    echo "wait for it to finish, then double-click RetroWorks again."
    xcode-select --install >/dev/null 2>&1
    read -r -p "Press Return to close this window. "
    exit 1
fi
/usr/bin/python3 retroworks.py "$@"

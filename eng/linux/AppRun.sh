#!/usr/bin/env bash
set -euo pipefail

# AppImage sets APPDIR; an extracted image can also run this entry point.
if [[ -z "${APPDIR:-}" ]]; then
    APPDIR="$(dirname -- "$(readlink -f -- "${BASH_SOURCE[0]}")")"
fi

export PATH="${APPDIR}:${APPDIR}/usr/sbin${PATH:+:${PATH}}"
export XDG_DATA_DIRS="${APPDIR}/usr/share${XDG_DATA_DIRS:+:${XDG_DATA_DIRS}}:/usr/share/gnome:/usr/local/share:/usr/share"
export LD_LIBRARY_PATH="${APPDIR}/usr/lib${LD_LIBRARY_PATH:+:${LD_LIBRARY_PATH}}"

# Electron must enforce its sandbox. Namespace or AppArmor denial must fail at
# launch rather than silently weaken isolation in the packaging entry point.
exec "${APPDIR}/com.lorekeeper.app" "$@"

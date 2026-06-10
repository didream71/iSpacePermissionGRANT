#!/bin/bash

# Grant ALL requested permissions + special appops (SYSTEM_ALERT_WINDOW etc.)
TIMEOUT_SEC=2
LIST_TIMEOUT=15

# -------------------------------------------------------------
# 1. Find adb
# -------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "$0")" && pwd)"
ADB="$SCRIPT_DIR/adb"

if [ ! -x "$ADB" ]; then
    if command -v adb >/dev/null 2>&1; then
        ADB="adb"
    else
        read -p "Enter full path to adb: " ADB_PATH
        [ -x "$ADB_PATH" ] || { echo "Not executable: $ADB_PATH"; exit 1; }
        ADB="$ADB_PATH"
    fi
fi
echo "Using adb: $ADB" && echo

# -------------------------------------------------------------
# 2. Connect to device
# -------------------------------------------------------------
check_device() { "$ADB" devices | grep -q "device$"; }

echo "== Checking USB devices =="
"$ADB" devices && echo

if ! check_device; then
    read -p "Enter IP (or Enter to exit): " USER_IP
    [ -z "$USER_IP" ] && exit 1
    "$ADB" connect "$USER_IP" || exit 1
    sleep 2
    "$ADB" devices
    check_device || { echo "Device not found"; exit 1; }
fi

echo "Testing device..."
timeout 5 "$ADB" shell echo ok >/dev/null || { echo "No response"; exit 1; }
echo "Device ready." && echo

# -------------------------------------------------------------
# 3. List 3rd-party packages
# -------------------------------------------------------------
echo "== Fetching 3rd-party packages =="
TMP_LIST="/tmp/pkgs_$$.txt"

timeout $LIST_TIMEOUT "$ADB" shell pm list packages -3 > "$TMP_LIST" 2>/dev/null
if [ ! -s "$TMP_LIST" ]; then
    echo "No 3rd-party packages, trying all..."
    timeout $LIST_TIMEOUT "$ADB" shell pm list packages > "$TMP_LIST" 2>/dev/null
fi

if [ ! -s "$TMP_LIST" ]; then
    echo "Failed to get package list."
    rm -f "$TMP_LIST"
    exit 1
fi

mapfile -t PKG_ARRAY < <(cut -d: -f2- "$TMP_LIST" | grep -v '^$')
rm -f "$TMP_LIST"
COUNT=${#PKG_ARRAY[@]}

if [ $COUNT -eq 0 ]; then
    echo "No packages found."
    exit 1
fi

for i in "${!PKG_ARRAY[@]}"; do
    echo "$((i+1)). ${PKG_ARRAY[$i]}"
done

echo
read -p "Enter number or package name: " CHOICE

if [[ "$CHOICE" =~ ^[0-9]+$ ]] && [ "$CHOICE" -ge 1 ] && [ "$CHOICE" -le "$COUNT" ]; then
    PKG="${PKG_ARRAY[$((CHOICE-1))]}"
else
    PKG="$CHOICE"
fi
echo "Selected: $PKG" && echo

# -------------------------------------------------------------
# 4. Get ALL requested permissions via dumpsys package
# -------------------------------------------------------------
echo "== Reading requested permissions for $PKG =="
DUMP_FILE="/tmp/dumpsys_$$.txt"
timeout $LIST_TIMEOUT "$ADB" shell dumpsys package "$PKG" > "$DUMP_FILE" 2>/dev/null

PERMS_LIST=$(sed -n '/requested permissions:/,/install permissions:/p' "$DUMP_FILE" \
    | grep -E '^[[:space:]]+[a-zA-Z0-9._]+' \
    | sed -E 's/^[[:space:]]+//; s/:.*$//')

if [ -z "$PERMS_LIST" ]; then
    PERMS_LIST=$(sed -n '/requested permissions:/,/^$/p' "$DUMP_FILE" \
        | grep -E '^[[:space:]]+[a-zA-Z0-9._]+' \
        | sed -E 's/^[[:space:]]+//; s/:.*$//')
fi

if [ -z "$PERMS_LIST" ]; then
    echo "  Could not parse dumpsys, using fallback set."
    PERMS_LIST="android.permission.READ_EXTERNAL_STORAGE
android.permission.WRITE_EXTERNAL_STORAGE
android.permission.POST_NOTIFICATIONS
android.permission.ACCESS_FINE_LOCATION
android.permission.ACCESS_COARSE_LOCATION
android.permission.ACCESS_BACKGROUND_LOCATION
android.permission.READ_PHONE_STATE
android.permission.GET_ACCOUNTS
android.permission.REQUEST_INSTALL_PACKAGES"
fi

mapfile -t PERM_ARRAY <<< "$PERMS_LIST"
echo "  Found $(echo "$PERMS_LIST" | wc -l) permissions:"
for p in "${PERM_ARRAY[@]}"; do
    echo "    $p"
done
rm -f "$DUMP_FILE"

# -------------------------------------------------------------
# 5. Helper function
# -------------------------------------------------------------
adb_cmd() {
    echo "[${TIMEOUT_SEC}s] $*"
    timeout $TIMEOUT_SEC "$ADB" "$@" 2>/dev/null
}

# -------------------------------------------------------------
# 6. Grant permissions: pm grant OR appops
# -------------------------------------------------------------
echo
echo "== Granting permissions =="

# Map Android permission -> appops name
declare -A APPOPS_MAP=(
    ["android.permission.SYSTEM_ALERT_WINDOW"]="SYSTEM_ALERT_WINDOW"
    ["android.permission.WRITE_SETTINGS"]="WRITE_SETTINGS"
    ["android.permission.REQUEST_INSTALL_PACKAGES"]="REQUEST_INSTALL_PACKAGES"
    ["android.permission.MANAGE_EXTERNAL_STORAGE"]="MANAGE_EXTERNAL_STORAGE"
    ["android.permission.GET_USAGE_STATS"]="GET_USAGE_STATS"
)

for perm in "${PERM_ARRAY[@]}"; do
    if [[ -n "${APPOPS_MAP[$perm]}" ]]; then
        appop="${APPOPS_MAP[$perm]}"
        echo "  (via appops) $perm -> $appop"
        adb_cmd shell appops set "$PKG" "$appop" allow
    else
        adb_cmd shell pm grant "$PKG" "$perm"
    fi
done

# -------------------------------------------------------------
# 7. Extra appops (always set those, just in case)
# -------------------------------------------------------------
echo
echo "== Ensuring critical appops =="
for op in SYSTEM_ALERT_WINDOW WRITE_SETTINGS MANAGE_EXTERNAL_STORAGE REQUEST_INSTALL_PACKAGES GET_USAGE_STATS; do
    adb_cmd shell appops set "$PKG" "$op" allow
done

# -------------------------------------------------------------
# 8. Verification
# -------------------------------------------------------------
echo
echo "== AppOps status =="
for op in SYSTEM_ALERT_WINDOW WRITE_SETTINGS MANAGE_EXTERNAL_STORAGE REQUEST_INSTALL_PACKAGES GET_USAGE_STATS; do
    adb_cmd shell appops get "$PKG" "$op"
done

echo
echo "== Runtime permissions (from dumpsys) =="
timeout $LIST_TIMEOUT "$ADB" shell dumpsys package "$PKG" | grep -A 20 "runtime permissions:" | head -25

# -------------------------------------------------------------
# 9. Restart and launch
# -------------------------------------------------------------
echo
echo "== Restarting $PKG =="
adb_cmd shell am force-stop "$PKG"
sleep 1

echo "== Launching $PKG =="
adb_cmd shell monkey -p "$PKG" -c android.intent.category.LAUNCHER 1

echo
echo "========================================"
echo "DONE. Overlay windows (SYSTEM_ALERT_WINDOW) enabled."
echo "========================================"
read -p "Press Enter to exit..."
exit 0

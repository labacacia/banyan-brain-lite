; Banyan Lite — NSIS installer script
; Build on Linux:   makensis banyan.nsi
; Build on Windows: makensis.exe banyan.nsi
; Version override: makensis -DVERSION=1.2.3 banyan.nsi
;
; Requires NSIS ≥ 3.0 (MUI2 is bundled with NSIS).
; On Linux: sudo apt install nsis

Unicode true
SetCompressor /SOLID lzma

; ── Version ──────────────────────────────────────────────────────────────────
!ifndef VERSION
  !define VERSION "1.1.0"
!endif

; ── Metadata ─────────────────────────────────────────────────────────────────
!define PRODUCT_NAME      "Banyan Lite"
!define PUBLISHER         "Inno Lotus Pty Ltd"
!define INSTALL_REG_KEY   "SOFTWARE\BanyanBrain\Lite"
!define UNINSTALL_REG_KEY "SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\BanyanLite"
!define PRODUCT_EXE       "banyan.exe"

; Source files directory (relative to this script, or pass -DPUBLISH_DIR=...)
!ifndef PUBLISH_DIR
  !define PUBLISH_DIR "..\..\publish\win-x64"
!endif

; ── Modern UI ────────────────────────────────────────────────────────────────
!include "MUI2.nsh"
!include "WinMessages.nsh"

!define MUI_ABORTWARNING
!define MUI_WELCOMEPAGE_TITLE     "Welcome to Banyan Lite Setup"
!define MUI_WELCOMEPAGE_TEXT      "This wizard will install Banyan Lite ${VERSION} on your computer.$\r$\n$\r$\nBanyan Lite is a personal AI memory and agent node.$\r$\n$\r$\nClick Next to continue."
!define MUI_FINISHPAGE_RUN        "$INSTDIR\${PRODUCT_EXE}"
!define MUI_FINISHPAGE_RUN_TEXT   "Launch Banyan Web"
!define MUI_FINISHPAGE_RUN_PARAMETERS "web"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_LICENSE    "license.rtf"
!insertmacro MUI_PAGE_DIRECTORY
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES
!insertmacro MUI_UNPAGE_FINISH

!insertmacro MUI_LANGUAGE "English"

; ── Installer attributes ──────────────────────────────────────────────────────
Name              "${PRODUCT_NAME} ${VERSION}"
OutFile           "banyan-lite-${VERSION}-setup.exe"
InstallDir        "$LOCALAPPDATA\Programs\${PRODUCT_NAME}"
InstallDirRegKey  HKCU "${INSTALL_REG_KEY}" "InstallDir"
RequestExecutionLevel user
ShowInstDetails   show
ShowUninstDetails show

; ─────────────────────────────────────────────────────────────────────────────
; INSTALL
; ─────────────────────────────────────────────────────────────────────────────
Section "Banyan Lite (required)" SecMain
  SectionIn RO
  SetOutPath "$INSTDIR"

  File /r "${PUBLISH_DIR}\*.*"

  ; Write uninstaller
  WriteUninstaller "$INSTDIR\uninstall.exe"

  ; Registry: install location
  WriteRegStr HKCU "${INSTALL_REG_KEY}" "InstallDir" "$INSTDIR"
  WriteRegStr HKCU "${INSTALL_REG_KEY}" "Version"    "${VERSION}"

  ; Add/Remove Programs entry
  WriteRegStr   HKCU "${UNINSTALL_REG_KEY}" "DisplayName"          "${PRODUCT_NAME}"
  WriteRegStr   HKCU "${UNINSTALL_REG_KEY}" "DisplayVersion"       "${VERSION}"
  WriteRegStr   HKCU "${UNINSTALL_REG_KEY}" "Publisher"            "${PUBLISHER}"
  WriteRegStr   HKCU "${UNINSTALL_REG_KEY}" "InstallLocation"      "$INSTDIR"
  WriteRegStr   HKCU "${UNINSTALL_REG_KEY}" "UninstallString"      '"$INSTDIR\uninstall.exe"'
  WriteRegStr   HKCU "${UNINSTALL_REG_KEY}" "QuietUninstallString" '"$INSTDIR\uninstall.exe" /S'
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "NoModify"             1
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "NoRepair"             1
  WriteRegDWORD HKCU "${UNINSTALL_REG_KEY}" "EstimatedSize"        140000

  ; Add install dir to the user's PATH via PowerShell (idempotent)
  ; Note: $$ is the NSIS escape for a literal $, needed for PowerShell variables.
  ;       $INSTDIR is an NSIS variable and expands before PowerShell sees the command.
  ExecWait 'powershell -NoProfile -Command "$$p = [Environment]::GetEnvironmentVariable(\"PATH\",\"User\"); if ($$p -notlike \"*$INSTDIR*\") { [Environment]::SetEnvironmentVariable(\"PATH\", $$p + \";$INSTDIR\", \"User\") }"'
SectionEnd

Section "Start Menu shortcuts" SecShortcuts
  CreateDirectory "$SMPROGRAMS\${PRODUCT_NAME}"
  CreateShortcut  "$SMPROGRAMS\${PRODUCT_NAME}\Banyan Web.lnk" \
                  "$INSTDIR\${PRODUCT_EXE}" "web" \
                  "$INSTDIR\${PRODUCT_EXE}" 0
  CreateShortcut  "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall ${PRODUCT_NAME}.lnk" \
                  "$INSTDIR\uninstall.exe"
SectionEnd

; ─────────────────────────────────────────────────────────────────────────────
; UNINSTALL
; ─────────────────────────────────────────────────────────────────────────────
Section "Uninstall"
  ; Stop running process (ignore errors)
  ExecWait 'taskkill /F /IM "${PRODUCT_EXE}"' $0

  ; Remove files
  Delete "$INSTDIR\uninstall.exe"
  RMDir /r "$INSTDIR"

  ; Start Menu
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\Banyan Web.lnk"
  Delete "$SMPROGRAMS\${PRODUCT_NAME}\Uninstall ${PRODUCT_NAME}.lnk"
  RMDir  "$SMPROGRAMS\${PRODUCT_NAME}"

  ; Remove install dir from the user's PATH via PowerShell
  ExecWait 'powershell -NoProfile -Command "$$p = [Environment]::GetEnvironmentVariable(\"PATH\",\"User\"); $$parts = $$p -split \";\" | Where-Object { $$_ -ne \"$INSTDIR\" }; [Environment]::SetEnvironmentVariable(\"PATH\", ($$parts -join \";\"), \"User\")"'

  ; Registry
  DeleteRegKey HKCU "${UNINSTALL_REG_KEY}"
  DeleteRegKey HKCU "${INSTALL_REG_KEY}"
SectionEnd

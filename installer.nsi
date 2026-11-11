; Muse Desktop 1.6.4 — NSIS installer (per-user, no admin)
Unicode true

!include "MUI2.nsh"

Name "Muse Desktop"
OutFile "MuseDesktop-Setup-NSIS.exe"
InstallDir "$LOCALAPPDATA\Programs\MuseDesktop"
RequestExecutionLevel user
SetCompressor /SOLID lzma

!define MUI_ICON "src\app.ico"
!define MUI_UNICON "src\app.ico"
!define MUI_FINISHPAGE_RUN "$INSTDIR\MuseDesktop.exe"
!define MUI_FINISHPAGE_RUN_TEXT "Chay Muse Desktop ngay"

VIProductVersion "1.6.4.0"
VIAddVersionKey "ProductName" "Muse Desktop"
VIAddVersionKey "FileDescription" "Muse Desktop Setup"
VIAddVersionKey "FileVersion" "1.6.4.0"

!insertmacro MUI_PAGE_WELCOME
!insertmacro MUI_PAGE_INSTFILES
!insertmacro MUI_PAGE_FINISH

!insertmacro MUI_UNPAGE_CONFIRM
!insertmacro MUI_UNPAGE_INSTFILES

!insertmacro MUI_LANGUAGE "Vietnamese"
!insertmacro MUI_LANGUAGE "English"

Section "Install"
  ; Close a running instance first so its DLLs are not locked.
  ExecWait 'taskkill /IM MuseDesktop.exe /F'
  Sleep 600

  SetOutPath "$INSTDIR"
  File /r "staging2\*.*"

  ; Install the WebView2 runtime only when it is really missing.
  ReadRegStr $0 HKLM "SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  StrCmp $0 "" 0 wv2done
  ReadRegStr $0 HKCU "Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}" "pv"
  StrCmp $0 "" 0 wv2done
  ExecWait '"$INSTDIR\MicrosoftEdgeWebView2Setup.exe" /silent /install'
  wv2done:

  CreateShortcut "$DESKTOP\Muse.lnk" "$INSTDIR\MuseDesktop.exe"
  CreateShortcut "$SMPROGRAMS\Muse.lnk" "$INSTDIR\MuseDesktop.exe"

  WriteUninstaller "$INSTDIR\Uninstall.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "DisplayName" "Muse Desktop"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "DisplayVersion" "1.6.4"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "DisplayIcon" "$INSTDIR\MuseDesktop.exe"
  WriteRegStr HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop" "UninstallString" '"$INSTDIR\Uninstall.exe"'
SectionEnd

Section "Uninstall"
  ExecWait 'taskkill /IM MuseDesktop.exe /F'
  Sleep 600
  Delete "$DESKTOP\Muse.lnk"
  Delete "$SMPROGRAMS\Muse.lnk"
  DeleteRegValue HKCU "Software\Microsoft\Windows\CurrentVersion\Run" "MuseDesktop"
  DeleteRegKey HKCU "Software\Microsoft\Windows\CurrentVersion\Uninstall\MuseDesktop"
  RMDir /r /REBOOTOK "$INSTDIR"
SectionEnd

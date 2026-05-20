; EnvVarUpdate — NSIS PATH manipulation helper
; Source: https://nsis.sourceforge.net/Environmental_Variables:_append,_prepend,_and_remove_entries
; License: zlib/libpng

!ifndef ENVVARUPDATE_INCLUDED
!define ENVVARUPDATE_INCLUDED

!include "LogicLib.nsh"
!include "WinMessages.NSH"
!include "StrFunc.nsh"

${StrTrimNewLines}
${UnStrTrimNewLines}

; AddToPath — append $INSTDIR to system or user PATH (used internally)
; Usage: ${EnvVarUpdate} $0 "PATH" "A" "HKLM" "$INSTDIR"
;        ${un.EnvVarUpdate} $0 "PATH" "R" "HKLM" "$INSTDIR"

!macro _EnvVarUpdate UN OUTVAR ENVVAR ACTION REGHIVE VALUE
  Push "${VALUE}"
  Push "${ACTION}"
  Push "${REGHIVE}"
  Push "${ENVVAR}"
  Call ${UN}EnvVarUpdate
  Pop "${OUTVAR}"
!macroend

!define EnvVarUpdate    '!insertmacro "_EnvVarUpdate" ""'
!define un.EnvVarUpdate '!insertmacro "_EnvVarUpdate" "un."'

!macro EnvVarUpdateBody UN
Function ${UN}EnvVarUpdate
  Push $0
  Exch 4
  Pop $0  ; EnvVar name
  Exch 3
  Push $1
  Exch 4
  Pop $1  ; RegHive
  Exch 3
  Push $2
  Exch 4
  Pop $2  ; Action (A=append, R=remove)
  Exch 3
  Push $3
  Exch 4
  Pop $3  ; Value to add/remove

  ; Read current value
  ReadRegStr $4 $1 "SYSTEM\CurrentControlSet\Control\Session Manager\Environment" "$0"

  ${If} $2 == "A"
    ; Check if already present (case-insensitive)
    Push $4
    Push $3
    Call ${UN}StrContains
    Pop $5
    ${If} $5 == ""
      ${If} $4 == ""
        StrCpy $4 "$3"
      ${Else}
        StrCpy $4 "$4;$3"
      ${EndIf}
      WriteRegExpandStr $1 "SYSTEM\CurrentControlSet\Control\Session Manager\Environment" "$0" "$4"
      SendMessage ${HWND_BROADCAST} ${WM_WININICHANGE} 0 "STR:Environment" /TIMEOUT=5000
    ${EndIf}
  ${ElseIf} $2 == "R"
    ; Remove value from PATH
    StrCpy $5 ""
    StrCpy $6 $4
    ${Do}
      ${StrTok} $7 $6 ";" "0" "1"
      ${If} $7 == ""
        ${Break}
      ${EndIf}
      ${If} $7 != $3
        ${If} $5 == ""
          StrCpy $5 "$7"
        ${Else}
          StrCpy $5 "$5;$7"
        ${EndIf}
      ${EndIf}
      StrLen $8 "$7;"
      StrCpy $6 $6 "" $8
    ${Loop}
    WriteRegExpandStr $1 "SYSTEM\CurrentControlSet\Control\Session Manager\Environment" "$0" "$5"
    SendMessage ${HWND_BROADCAST} ${WM_WININICHANGE} 0 "STR:Environment" /TIMEOUT=5000
  ${EndIf}

  Push $4
  Exch
  Pop $0
  Exch
  Pop $1
  Exch
  Pop $2
  Exch
  Pop $3
FunctionEnd

Function ${UN}StrContains
  Exch $R0  ; needle
  Exch
  Exch $R1  ; haystack
  Push $R2
  Push $R3
  StrLen $R3 $R0
  StrCpy $R2 0
  ${Do}
    StrCpy $R4 $R1 $R3 $R2
    ${If} $R4 == $R0
      StrCpy $R0 "found"
      ${Break}
    ${EndIf}
    ${If} $R4 == ""
      StrCpy $R0 ""
      ${Break}
    ${EndIf}
    IntOp $R2 $R2 + 1
  ${Loop}
  Pop $R3
  Pop $R2
  Pop $R1
  Exch $R0
FunctionEnd
!macroend

!insertmacro EnvVarUpdateBody ""
!insertmacro EnvVarUpdateBody "un."

!endif ; ENVVARUPDATE_INCLUDED

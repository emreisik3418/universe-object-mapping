@echo off
rem ===========================================================================
rem  Universe object + table mapping: once UNX (Java), sonra UNV (C#).
rem  Iki uygulama da POPR.SDK_METADATA_UNIVERSEOM_JOB ve ..._UNIVERSETM_JOB
rem  tablolarini gunceller. Biri hata verse de digeri calisir.
rem
rem  Cikis kodu: 0 ikisi de basarili, 1 en az birinde kismi hata,
rem              2 en az biri hic baslayamadi.
rem  Loglar: UNX -> %APP%\work\logs   UNV -> %APP%\unv\logs
rem  Lisans (580) gunduz doldugu icin mesai sonrasi calistirin.
rem ===========================================================================
setlocal

set BO=C:\Program Files (x86)\SAP BusinessObjects\SAP BusinessObjects Enterprise XI 4.0
set APP=C:\batchs\BusinessObjects\UNIVERSEOBJECTMAPPING
set CFG=%APP%\config\credentials.json

rem --- UNX (Java) ayarlari
set JAVA=%BO%\win64_x64\sapjvm\bin\java.exe
set CP=%APP%\lib\universe-object-mapping.jar;%BO%\SL SDK\java\sl_sdk.jar;%APP%\lib\axis\*;%APP%\lib\oracle\*;%APP%\lib\json\*

rem --- UNV (C#) ayarlari: bin\x86\Debug klasorunun TAMAMI buraya kopyalanir
set UNV_DIR=%APP%\unv
set UNV_EXE=%UNV_DIR%\UnvObjectMapping.exe

if not exist "%CFG%" (
  echo [HATA] credentials.json yok: %CFG%
  exit /b 2
)
if not exist "%APP%\work" mkdir "%APP%\work"

rem ============================== 1) UNX ======================================
echo.
echo [%date% %time%] UNX (Java) basladi
set UNX_RC=2
if not exist "%APP%\lib\universe-object-mapping.jar" (
  echo [HATA] Jar yok: %APP%\lib\universe-object-mapping.jar
) else (
  cd /d "%APP%\work"
  "%JAVA%" -Xmx2g ^
    -Djava.library.path="%BO%\win64_x64" ^
    -Dbusinessobjects.connectivity.directory="%BO%\dataAccess\connectionServer" ^
    -cp "%CP%" com.akbank.analytics.applications.bo.app.UniverseObjectMappingApp "%CFG%"
  call set UNX_RC=%%ERRORLEVEL%%
)
echo [%date% %time%] UNX bitti. Cikis kodu: %UNX_RC%

rem ============================== 2) UNV ======================================
echo.
echo [%date% %time%] UNV (C#) basladi
set UNV_RC=2
if not exist "%UNV_EXE%" (
  echo [HATA] Exe yok: %UNV_EXE%
) else (
  cd /d "%UNV_DIR%"
  "%UNV_EXE%" run "%CFG%"
  call set UNV_RC=%%ERRORLEVEL%%
)
echo [%date% %time%] UNV bitti. Cikis kodu: %UNV_RC%

rem ============================== Sonuc ========================================
set RC=0
if %UNX_RC% GTR %RC% set RC=%UNX_RC%
if %UNV_RC% GTR %RC% set RC=%UNV_RC%
echo.
echo [%date% %time%] TAMAMLANDI. UNX=%UNX_RC%  UNV=%UNV_RC%  Genel=%RC%
endlocal & exit /b %RC%

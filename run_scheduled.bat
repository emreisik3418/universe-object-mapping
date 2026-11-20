@echo off
rem ===========================================================================
rem  ZAMANLANMIS CALISTIRMA (app launcher / Gorev Zamanlayici icin)
rem  1) UNX (Java)  object + table mapping
rem  2) UNV (C#)    object + table mapping
rem  3) UNV'de hata alan universe varsa: retry (1 kez)
rem  4) 30 gunden eski loglari siler
rem
rem  Tum cikti: %APP%\logs\scheduled_YYYYMMDD_HHMMSS.log (bitince ekrana da basilir)
rem  Soru sormaz, pause yok; launcher cikis kodunu okuyabilir.
rem  Cikis kodu: 0 hepsi basarili, 1 kismi hata, 2 en az biri hic baslayamadi
rem
rem  TEST MODU:  run_scheduled.bat test
rem    DB'ye HICBIR SEY yazmaz, birkac dakika surer. Java ve C# icin logon +
rem    tek universe okuma yapar (yollar, Sbox, CMS, Designer, log, cikis kodu testi).
rem ===========================================================================
setlocal

set BO=C:\Program Files (x86)\SAP BusinessObjects\SAP BusinessObjects Enterprise XI 4.0
set APP=C:\batchs\BusinessObjects\UNIVERSEOBJECTMAPPING
set CFG=%APP%\config\credentials.json

set JAVA=%BO%\win64_x64\sapjvm\bin\java.exe
set CP=%APP%\lib\universe-object-mapping.jar;%BO%\SL SDK\java\sl_sdk.jar;%APP%\lib\axis\*;%APP%\lib\oracle\*;%APP%\lib\json\*
set UNV_DIR=%APP%\unv
set UNV_EXE=%UNV_DIR%\UnvObjectMapping.exe
set KEEP_DAYS=30

rem --- Kendini log dosyasina yonlendirerek tekrar cagir
if "%~1"=="--inner" goto :main
set MODE=run
if /i "%~1"=="test" set MODE=test
if not exist "%APP%\logs" mkdir "%APP%\logs"
for /f %%i in ('powershell -NoProfile -Command "Get-Date -Format yyyyMMdd_HHmmss"') do set TS=%%i
set LOG=%APP%\logs\scheduled_%TS%.log
if "%MODE%"=="test" set LOG=%APP%\logs\scheduled_TEST_%TS%.log
echo [%date% %time%] Basladi (%MODE%). Log: %LOG%
call "%~f0" --inner %MODE% >> "%LOG%" 2>&1
set RC=%ERRORLEVEL%
rem Launcher / konsol ciktiyi da gorsun diye log tamamini ekrana bas
type "%LOG%"
endlocal & exit /b %RC%

:main
echo ===========================================================================
echo [%date% %time%] BASLADI
echo ===========================================================================
if not exist "%CFG%" (
  echo [HATA] credentials.json yok: %CFG%
  exit /b 2
)
if not exist "%APP%\work" mkdir "%APP%\work"
if /i "%~2"=="test" goto :test

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
  goto :cleanup
)
cd /d "%UNV_DIR%"
"%UNV_EXE%" run "%CFG%"
set UNV_RC=%ERRORLEVEL%
echo [%date% %time%] UNV bitti. Cikis kodu: %UNV_RC%

rem ============================== 3) UNV retry =================================
rem  Kod 1 = bazi universe'ler okunamadi, 2 = bir ortam tamamen basarisiz (or. lisans).
rem  Retry, logs\ altindaki en yeni unv_*.log dosyasini (az once biten run'in log'u)
rem  okuyup sadece hatalilari / eksikleri dener.
if not "%UNV_RC%"=="0" (
  echo.
  echo [%date% %time%] UNV retry basladi ^(10 dk bekleme sonrasi^)
  timeout /t 600 /nobreak >nul
  "%UNV_EXE%" retry "%CFG%"
  call set UNV_RC=%%ERRORLEVEL%%
  call echo [%%date%% %%time%%] UNV retry bitti. Cikis kodu: %%UNV_RC%%
)

:cleanup
rem ============================== 4) Eski loglar ===============================
echo.
echo [%date% %time%] %KEEP_DAYS% gunden eski loglar siliniyor
for %%D in ("%APP%\logs" "%APP%\work\logs" "%UNV_DIR%\logs") do (
  if exist "%%~D" forfiles /p "%%~D" /m *.log /d -%KEEP_DAYS% /c "cmd /c del @path" 2>nul
)

rem ============================== Sonuc ========================================
set RC=0
if %UNX_RC% GTR %RC% set RC=%UNX_RC%
if %UNV_RC% GTR %RC% set RC=%UNV_RC%
echo.
echo ===========================================================================
echo [%date% %time%] TAMAMLANDI. UNX=%UNX_RC%  UNV=%UNV_RC%  Genel=%RC%
echo ===========================================================================
exit /b %RC%

rem ============================== TEST MODU ====================================
rem  DB'ye yazmaz. Java: logon + ilk UNX'i okuma. C#: logon + ilk UNV'yi Designer ile okuma.
:test
echo [%date% %time%] TEST MODU - DB'ye yazilmayacak
set UNX_RC=2
set UNV_RC=2

echo.
echo [%date% %time%] UNX test: logon
if not exist "%APP%\lib\universe-object-mapping.jar" (
  echo [HATA] Jar yok: %APP%\lib\universe-object-mapping.jar
  goto :test_unv
)
cd /d "%APP%\work"
"%JAVA%" -Xmx2g ^
  -Djava.library.path="%BO%\win64_x64" ^
  -Dbusinessobjects.connectivity.directory="%BO%\dataAccess\connectionServer" ^
  -cp "%CP%" com.akbank.analytics.applications.bo.app.BoLogonCheck "%CFG%"
set UNX_RC=%ERRORLEVEL%
echo [%date% %time%] UNX test: tek universe okuma
"%JAVA%" -Xmx2g ^
  -Djava.library.path="%BO%\win64_x64" ^
  -Dbusinessobjects.connectivity.directory="%BO%\dataAccess\connectionServer" ^
  -cp "%CP%" com.akbank.analytics.applications.bo.app.UniverseReadCheck "%CFG%"
if not "%ERRORLEVEL%"=="0" set UNX_RC=1
echo [%date% %time%] UNX test bitti. Cikis kodu: %UNX_RC%

:test_unv
echo.
echo [%date% %time%] UNV test: logon
if not exist "%UNV_EXE%" (
  echo [HATA] Exe yok: %UNV_EXE%
  goto :test_end
)
cd /d "%UNV_DIR%"
"%UNV_EXE%" logon "%CFG%"
set UNV_RC=%ERRORLEVEL%
echo [%date% %time%] UNV test: Designer ile tek universe okuma
"%UNV_EXE%" read "%CFG%"
if not "%ERRORLEVEL%"=="0" set UNV_RC=1
echo [%date% %time%] UNV test bitti. Cikis kodu: %UNV_RC%

:test_end
set RC=0
if %UNX_RC% GTR %RC% set RC=%UNX_RC%
if %UNV_RC% GTR %RC% set RC=%UNV_RC%
echo.
echo ===========================================================================
echo [%date% %time%] TEST TAMAMLANDI. UNX=%UNX_RC%  UNV=%UNV_RC%  Genel=%RC%
echo ===========================================================================
exit /b %RC%

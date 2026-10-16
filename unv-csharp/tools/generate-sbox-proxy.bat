@echo off
rem ------------------------------------------------------------------
rem  Sbox (HsmService) WCF istemci sınıflarını WSDL'den üretir.
rem  Çıktı: UnvObjectMapping\Sbox\HsmServiceProxy.cs  (namespace UnvObjectMapping.Sbox)
rem
rem  Kullanım:
rem    generate-sbox-proxy.bat <WSDL adresi veya dosya yolu>
rem  Örnek:
rem    generate-sbox-proxy.bat "http://SBOX-SUNUCU/HsmService.svc?wsdl"
rem    generate-sbox-proxy.bat C:\dev\libs\sbox\HsmService.wsdl
rem
rem  Üretilen dosya .gitignore'da; her makinede bir kez çalıştırmanız yeterli.
rem ------------------------------------------------------------------
setlocal

if "%~1"=="" (
  echo Kullanim: %~nx0 ^<WSDL adresi veya dosya yolu^>
  exit /b 2
)

set "OUT=%~dp0..\UnvObjectMapping\Sbox\HsmServiceProxy.cs"

rem svcutil'i bul: önce PATH (Developer Command Prompt), sonra .NET 4.8 SDK araçları
set "SVCUTIL="
for /f "delims=" %%i in ('where svcutil 2^>nul') do if not defined SVCUTIL set "SVCUTIL=%%i"
if not defined SVCUTIL if exist "C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\SvcUtil.exe" set "SVCUTIL=C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8 Tools\SvcUtil.exe"
if not defined SVCUTIL if exist "C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8.1 Tools\SvcUtil.exe" set "SVCUTIL=C:\Program Files (x86)\Microsoft SDKs\Windows\v10.0A\bin\NETFX 4.8.1 Tools\SvcUtil.exe"

if not defined SVCUTIL (
  echo [HATA] SvcUtil.exe bulunamadi. "Developer Command Prompt for VS" icinden calistirin
  echo        veya: dir /s /b "C:\Program Files (x86)\Microsoft SDKs\SvcUtil.exe"
  exit /b 1
)

echo SvcUtil: %SVCUTIL%
"%SVCUTIL%" "%~1" /language:C# /namespace:*,UnvObjectMapping.Sbox /syncOnly /noConfig /noLogo /out:"%OUT%"
if errorlevel 1 (
  echo [HATA] Proxy uretilemedi.
  exit /b 1
)

echo [OK] Uretildi: %OUT%
exit /b 0

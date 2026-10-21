@echo off
rem UNX obje açıklamalarını Oracle'daki sorgudan günceller.
rem Kullanım:
rem   run_description.bat <universe yolu veya CUID> <sorgu.sql> [--env HOST:6400] [--apply]
rem --apply verilmezse DRY-RUN: universe'e dokunulmaz, sadece reports\ altına CSV yazılır.
rem Çıkış kodu: 0 başarılı, 1 yayınlandı ama doğrulamada uyarı, 2 hata (yayın yapılmadı).
setlocal
set BO=C:\Program Files (x86)\SAP BusinessObjects\SAP BusinessObjects Enterprise XI 4.0
set APP=C:\batchs\BusinessObjects\UNIVERSEOBJECTMAPPING
set CP=%APP%\lib\universe-object-mapping.jar;%BO%\SL SDK\java\sl_sdk.jar;%APP%\lib\axis\*;%APP%\lib\oracle\*;%APP%\lib\json\*

if not exist "%APP%\work" mkdir "%APP%\work"
cd /d "%APP%\work"

"%BO%\win64_x64\sapjvm\bin\java.exe" -Xmx2g ^
  -Djava.library.path="%BO%\win64_x64" ^
  -Dbusinessobjects.connectivity.directory="%BO%\dataAccess\connectionServer" ^
  -cp "%CP%" com.akbank.analytics.applications.bo.description.DescriptionUpdateApp "%APP%\config\credentials.json" %*

exit /b %ERRORLEVEL%

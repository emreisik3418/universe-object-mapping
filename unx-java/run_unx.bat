@echo off
rem UNX object + table mapping (Java). Çıkış kodu: 0 başarılı, 1 kısmi hata, 2 başlayamadı.
setlocal
set BO=C:\Program Files (x86)\SAP BusinessObjects\SAP BusinessObjects Enterprise XI 4.0
set APP=C:\batchs\BusinessObjects\UNIVERSEOBJECTMAPPING
set CP=%APP%\lib\universe-object-mapping.jar;%BO%\SL SDK\java\sl_sdk.jar;%APP%\lib\axis\*;%APP%\lib\oracle\*;%APP%\lib\json\*

if not exist "%APP%\work" mkdir "%APP%\work"
cd /d "%APP%\work"

"%BO%\win64_x64\sapjvm\bin\java.exe" -Xmx2g ^
  -Djava.library.path="%BO%\win64_x64" ^
  -Dbusinessobjects.connectivity.directory="%BO%\dataAccess\connectionServer" ^
  -cp "%CP%" com.akbank.analytics.applications.bo.app.UniverseObjectMappingApp "%APP%\config\credentials.json"

exit /b %ERRORLEVEL%

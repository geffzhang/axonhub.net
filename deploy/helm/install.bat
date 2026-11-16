@echo off
setlocal

set RELEASE_NAME=%1
if "%RELEASE_NAME%"=="" set RELEASE_NAME=axonhub

set NAMESPACE=%2
if "%NAMESPACE%"=="" set NAMESPACE=default

echo Installing/upgrading AxonHub.NET...
echo Release:   %RELEASE_NAME%
echo Namespace: %NAMESPACE%

helm upgrade --install %RELEASE_NAME% "%~dp0" ^
  --namespace %NAMESPACE% ^
  --create-namespace ^
  --timeout 10m0s

if %ERRORLEVEL% NEQ 0 exit /b %ERRORLEVEL%

echo.
echo Installation completed.
echo Access with:
echo   kubectl port-forward svc/%RELEASE_NAME% 8090:8090 -n %NAMESPACE%
echo.
echo Check status: kubectl get pods -n %NAMESPACE%
echo Run test:     helm test %RELEASE_NAME% -n %NAMESPACE%
echo Uninstall:    helm uninstall %RELEASE_NAME% -n %NAMESPACE%

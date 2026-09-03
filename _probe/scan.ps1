Get-PnpDevice -PresentOnly | Where-Object { $_.InstanceId -match 'VID_1B1C' } | Format-List FriendlyName, Class, InstanceId, Status
Write-Host '=== All USB HID/Controller devices ==='
Get-PnpDevice -PresentOnly -Class USB, HIDClass | Format-List FriendlyName, InstanceId
Write-Host '=== iCUE install path ==='
(Get-Process -Name 'iCUE' -ErrorAction SilentlyContinue | Select-Object -First 1).Path
Write-Host '=== Corsair AIO detection via WMI ==='
Get-CimInstance Win32_PnPEntity | Where-Object { $_.Name -match 'Corsair|Hydro|H100|H115|H150|H60|iCUE' } | Format-List Name, DeviceID

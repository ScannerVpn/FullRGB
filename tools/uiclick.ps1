Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
# Clicks a control in the FullRGB window BY NAME using UI Automation.
# Coordinate clicking proved unreliable: the window had been moved off-screen and hard-coded
# offsets landed on the wrong row. UIA finds the element wherever it is and invokes it.
$want = $args[0]
$p = Get-Process FullRGB -ErrorAction Stop | Where-Object { $_.MainWindowHandle -ne 0 } | Select-Object -First 1
if (-not $p) { Write-Output 'NO_WINDOW'; exit 1 }
$root = [System.Windows.Automation.AutomationElement]::FromHandle($p.MainWindowHandle)
$cond = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::NameProperty, $want)
$el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
if (-not $el) { Write-Output ("NOT_FOUND: " + $want); exit 2 }

# RadioButton exposes SelectionItem, Button exposes Invoke; try both.
try {
    $si = $el.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $si.Select()
    Write-Output ("selected: " + $want)
    exit 0
} catch { }
try {
    $inv = $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $inv.Invoke()
    Write-Output ("invoked: " + $want)
    exit 0
} catch { }
Write-Output ("NO_PATTERN: " + $want)
exit 3

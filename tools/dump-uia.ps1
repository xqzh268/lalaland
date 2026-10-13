# Dumps the UI Automation tree of Apple Music windows (dev helper for locating the Favorite button)
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$procIds = (Get-Process AppleMusic -ErrorAction Stop).Id
$root = [System.Windows.Automation.AutomationElement]::RootElement
$cond = [System.Windows.Automation.Condition]::TrueCondition
$wins = $root.FindAll([System.Windows.Automation.TreeScope]::Children, $cond) | Where-Object { $procIds -contains $_.Current.ProcessId }
function Walk($el, $depth) {
  if ($depth -gt 25) { return }
  $c = $el.Current
  if ($c.Name -or $c.AutomationId) {
    $pats = ($el.GetSupportedPatterns() | ForEach-Object { $_.ProgrammaticName -replace 'PatternIdentifiers.Pattern','' }) -join ','
    Write-Output ("{0}[{1}] name='{2}' id='{3}' cls='{4}' {5}" -f ('  ' * $depth), $c.ControlType.ProgrammaticName.Replace('ControlType.',''), $c.Name, $c.AutomationId, $c.ClassName, $pats)
  }
  $w = [System.Windows.Automation.TreeWalker]::ControlViewWalker
  $ch = $w.GetFirstChild($el)
  while ($ch) { Walk $ch ($depth + 1); $ch = $w.GetNextSibling($ch) }
}
foreach ($w in $wins) { Walk $w 0 }

# Every PowerShell script named on the command line parses, read with PowerShell's own parser: the Windows jobs run these scripts,
# and a script that does not parse fails every step that calls it, so the Linux test job reads them all on every push first.
# Prints one line per script (or one per error, with its line), and exits with the number of scripts that did not parse.
$bad = 0
if ($args.Count -eq 0) { Write-Output 'no script was named'; exit 1 }
foreach ($path in $args) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile((Resolve-Path -LiteralPath $path).Path, [ref]$tokens, [ref]$errors)
    if ($errors.Count -gt 0) {
        $bad++
        foreach ($e in $errors) { Write-Output ('{0}:{1}: {2}' -f $path, $e.Extent.StartLineNumber, $e.Message) }
    }
    else {
        Write-Output "$path parses"
    }
}
exit $bad

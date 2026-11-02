# Ghidra helper.
#   .\ghidra.ps1                      -> opens the Ghidra GUI
#   .\ghidra.ps1 -Binary <path.dll>   -> headless analysis + SummarizeBinary.java report
# Sets JAVA_HOME for this session only; nothing is changed system-wide.
param(
    [string]$Binary,
    [string]$Project = 'SnippingTool'
)
$ErrorActionPreference = 'Stop'
$ghidra = 'C:\Tools\ghidra_12.1.4_PUBLIC'
$env:JAVA_HOME = 'C:\Tools\jdk-21.0.12.1+1'
$env:PATH = "$env:JAVA_HOME\bin;$env:PATH"

if (-not $Binary) {
    & "$ghidra\ghidraRun.bat"
    return
}

$projects = Join-Path $PSScriptRoot 'ghidra-projects'
New-Item -ItemType Directory -Force $projects | Out-Null
& "$ghidra\support\analyzeHeadless.bat" $projects $Project -import $Binary `
    -scriptPath (Join-Path $PSScriptRoot 'ghidra-scripts') -postScript SummarizeBinary.java -overwrite 2>&1 |
    ForEach-Object { "$_" } |
    Where-Object { $_ -match 'SummarizeBinary.java>' } |
    ForEach-Object { $_ -replace '^.*SummarizeBinary.java> ', '' -replace ' \(GhidraScript\)\s*$', '' }

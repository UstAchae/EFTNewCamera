param([string]$GameRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
Add-Type -Path "$PSHOME/Microsoft.CodeAnalysis.dll"
Add-Type -Path "$PSHOME/Microsoft.CodeAnalysis.CSharp.dll"
$trees = [Microsoft.CodeAnalysis.SyntaxTree[]]@(Get-ChildItem "$PSScriptRoot/*.cs" | ForEach-Object {
    [Microsoft.CodeAnalysis.CSharp.CSharpSyntaxTree]::ParseText(
        [IO.File]::ReadAllText($_.FullName),
        [Microsoft.CodeAnalysis.CSharp.CSharpParseOptions]::Default,
        $_.FullName, [Text.Encoding]::UTF8, [Threading.CancellationToken]::None)
})
$refs = [Microsoft.CodeAnalysis.MetadataReference[]]@(
    Get-ChildItem "$GameRoot/EscapeFromTarkov_Data/Managed/*.dll", "$GameRoot/BepInEx/core/BepInEx.dll", "$GameRoot/BepInEx/core/0Harmony.dll" | ForEach-Object {
        [Microsoft.CodeAnalysis.MetadataReference]::CreateFromFile($_.FullName)
    }
)
$options = [Microsoft.CodeAnalysis.CSharp.CSharpCompilationOptions]::new(
    [Microsoft.CodeAnalysis.OutputKind]::DynamicallyLinkedLibrary
).WithOptimizationLevel([Microsoft.CodeAnalysis.OptimizationLevel]::Release).WithNullableContextOptions([Microsoft.CodeAnalysis.NullableContextOptions]::Enable)
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create('EFTNewCamera', $trees, $refs, $options)
$output = Join-Path $PSScriptRoot '_build'
New-Item $output -ItemType Directory -Force | Out-Null
$stream = [IO.File]::Create((Join-Path $output 'EFTNewCamera.dll'))
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
$result.Diagnostics | ForEach-Object { $_.ToString() } | Set-Content (Join-Path $output 'build.log') -Encoding utf8
$result.Diagnostics | Where-Object Severity -eq Error | ForEach-Object { Write-Host $_.ToString() }
$result.Diagnostics | Group-Object Id, Severity | Select-Object Count, Name | Format-Table
if (!$result.Success) { throw 'Compilation failed; see _build/build.log.' }
Get-FileHash (Join-Path $output 'EFTNewCamera.dll') -Algorithm SHA256

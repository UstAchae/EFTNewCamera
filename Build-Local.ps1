param([string]$GameRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$name = 'JustachaeThirdPersonCamera'
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
$compilation = [Microsoft.CodeAnalysis.CSharp.CSharpCompilation]::Create($name, $trees, $refs, $options)
$output = Join-Path $PSScriptRoot '_build'
New-Item $output -ItemType Directory -Force | Out-Null
$dll = Join-Path $output "$name.dll"
$stream = [IO.File]::Create($dll)
try { $result = $compilation.Emit($stream) } finally { $stream.Dispose() }
$result.Diagnostics | ForEach-Object { $_.ToString() } | Set-Content (Join-Path $output 'build.log') -Encoding utf8
$result.Diagnostics | Where-Object Severity -eq Error | ForEach-Object { Write-Host $_.ToString() }
$result.Diagnostics | Group-Object Id, Severity | Select-Object Count, Name | Format-Table
if (!$result.Success) { throw 'Compilation failed; see _build/build.log.' }

# Unzipped into the SPT folder, the archive puts the plugin at BepInEx/plugins/<name>/<name>.dll.
$version = [regex]::Match([IO.File]::ReadAllText("$PSScriptRoot/NewCameraPlugin.cs"), 'PluginVersion = "([^"]+)"').Groups[1].Value
$stage = Join-Path $output 'package'
$buildRoot = [IO.Path]::GetFullPath($output).TrimEnd([IO.Path]::DirectorySeparatorChar)
$stage = [IO.Path]::GetFullPath($stage)
if ([IO.Path]::GetDirectoryName($stage) -ne $buildRoot) { throw 'Package staging directory is outside _build.' }
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
$pluginDir = Join-Path $stage "BepInEx/plugins/$name"
New-Item $pluginDir -ItemType Directory -Force | Out-Null
Copy-Item -LiteralPath $dll -Destination $pluginDir
Copy-Item -LiteralPath (Join-Path $PSScriptRoot "PORTING-3.11.4.md") -Destination $stage
$zip = Join-Path $output "$name-$version-SPT-3.11.4.zip"
Remove-Item $zip -Force -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($stage, $zip)
Get-FileHash $dll -Algorithm SHA256

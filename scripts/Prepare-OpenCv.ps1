param([string]$CMake = 'cmake', [ValidateRange(1, 64)][int]$Jobs = 8)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath("$PSScriptRoot/..")
$work = Join-Path $root ('work/opencv-build-' + [Guid]::NewGuid().ToString('N'))
$output = Join-Path $root 'artifacts/opencv/win-x64'
New-Item -ItemType Directory -Path $work, $output -Force | Out-Null

function Import-Source([string]$Name, [string]$Url, [string]$Sha256) {
    $archive = Join-Path $work "$Name.tar.gz"
    curl.exe --fail --silent --show-error --location --retry 2 $Url --output $archive
    if ($LASTEXITCODE -or (Get-FileHash -LiteralPath $archive).Hash -cne $Sha256) { throw "Invalid $Name source archive." }
    $source = Join-Path $work $Name
    New-Item -ItemType Directory -Path $source | Out-Null
    tar.exe -xf $archive -C $source --strip-components=1
    if ($LASTEXITCODE) { throw "Cannot extract $Name source." }
    return $source
}

$opencv = Import-Source 'opencv' 'https://codeload.github.com/opencv/opencv/tar.gz/31b0eeea0b44b370fd0712312df4214d4ae1b158' 'BB15D5C85BAB38C1377C0C7DD7FF201B078BCDB41CC4FF9F85AC61AA56B5F631'
$sharp = Import-Source 'opencvsharp' 'https://codeload.github.com/shimat/opencvsharp/tar.gz/2360cafffe47273dc659fb45dbb5bf07bdd65f85' 'E234FE220E0DEBCFF462D33C422EAEFCCD98C2CD1D7A996544257FEC5BBEF7C3'

# Keep the original wrapper entry points for the three image-processing modules.
$header = Join-Path $sharp 'src/OpenCvSharpExtern/include_opencv.h'
$original = [IO.File]::ReadAllText($header)
$trimmed = [regex]::Replace($original, '(?s)// MP! Added:.*?(?=#include <vector>)', "#include <opencv2/core/utils/logger.hpp>`n#include <opencv2/imgproc/imgproc_c.h>`n`n")
if ($trimmed -ceq $original) { throw 'OpenCvSharp header layout differs from the pinned revision.' }
[IO.File]::WriteAllText($header, $trimmed)
$vectors = Join-Path $sharp 'src/OpenCvSharpExtern/std_vector.h'
$original = [IO.File]::ReadAllText($vectors)
$trimmed = [regex]::Replace($original, '(?ms)^#pragma region cv::(?:ml::DTrees::(?:Node|Split)|detail::ImageFeatures)\r?\n.*?^#pragma endregion\r?\n', '')
if ($trimmed -ceq $original) { throw 'OpenCvSharp vector layout differs from the pinned revision.' }
[IO.File]::WriteAllText($vectors, $trimmed)

function Invoke-CMake([string[]]$Arguments) {
    & $CMake @Arguments
    if ($LASTEXITCODE) { throw "CMake failed. Build files: $work" }
}

$generator = @('-G', 'Visual Studio 18 2026', '-A', 'x64')
$options = @(
    '-DBUILD_LIST=core,imgproc,imgcodecs', '-DBUILD_SHARED_LIBS=OFF', '-DBUILD_WITH_STATIC_CRT=ON',
    '-DBUILD_TESTS=OFF', '-DBUILD_PERF_TESTS=OFF', '-DBUILD_EXAMPLES=OFF', '-DBUILD_JAVA=OFF',
    '-DBUILD_opencv_apps=OFF', '-DBUILD_opencv_python3=OFF', '-DBUILD_opencv_python2=OFF', '-DBUILD_opencv_python_bindings_generator=OFF',
    '-DWITH_IPP=OFF', '-DBUILD_IPP_IW=OFF', '-DWITH_ITT=OFF', '-DWITH_OPENCL=OFF', '-DWITH_LAPACK=OFF',
    '-DWITH_EIGEN=OFF', '-DWITH_FFMPEG=OFF', '-DWITH_MSMF=OFF', '-DWITH_OPENEXR=OFF', '-DWITH_OPENJPEG=OFF',
    '-DWITH_TIFF=OFF', '-DWITH_WEBP=OFF', '-DWITH_JASPER=OFF', '-DWITH_GDAL=OFF', '-DWITH_ADE=OFF',
    '-DWITH_PROTOBUF=OFF', '-DWITH_FLATBUFFERS=OFF',
    '-DCPU_BASELINE=SSE2', '-DCPU_DISPATCH=SSE4_1,SSE4_2,AVX,FP16,AVX2,AVX512_SKX'
)
Invoke-CMake (@('-S', $opencv, '-B', "$work/opencv-out", "-DCMAKE_INSTALL_PREFIX=$work/install") + $generator + $options)
Invoke-CMake @('--build', "$work/opencv-out", '--config', 'Release', '--target', 'INSTALL', '--parallel', "$Jobs")
Invoke-CMake (@('-S', "$PSScriptRoot/opencv", '-B', "$work/wrapper-out", "-DOpenCV_DIR=$work/install/staticlib", "-DOPENCVSHARP_SOURCE=$sharp") + $generator)
Invoke-CMake @('--build', "$work/wrapper-out", '--config', 'Release', '--parallel', "$Jobs")
$dll = "$work/wrapper-out/Release/OpenCvSharpExtern.dll"
$staged = Join-Path $output ([Guid]::NewGuid().ToString('N') + '.tmp')
# OpenCV's build information and __FILE__ diagnostics embed the build root.
# Replace only those string prefixes without moving bytes in the linked PE.
$encoding = [Text.Encoding]::Latin1
$image = $encoding.GetString([IO.File]::ReadAllBytes($dll))
foreach ($prefix in @($work.Replace('\', '/'), $work.Replace('/', '\'))) {
    $encoded = $encoding.GetString([Text.Encoding]::UTF8.GetBytes($prefix))
    $image = [regex]::Replace($image, [regex]::Escape($encoded), '.' * $encoded.Length, 'IgnoreCase')
}
if ($image -match '(?i)[a-z]:[/\\]+(?:users|my_projects|codex)[/\\]+') { throw 'A local path remains in the OpenCV binary.' }
[IO.File]::WriteAllBytes($staged, $encoding.GetBytes($image))
[IO.File]::Move($staged, (Join-Path $output 'OpenCvSharpExtern.dll'), $true)
Get-FileHash -LiteralPath (Join-Path $output 'OpenCvSharpExtern.dll') -Algorithm SHA256

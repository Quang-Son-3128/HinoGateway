# Hướng dẫn publish và triển khai HinoGateway lên IPC

Tài liệu vận hành (runbook) — đi từ máy lập trình đến khi HinoGateway chạy như một Windows service
trên máy tính công nghiệp (IPC) và được nghiệm thu.

> **Đã kiểm chứng ngày 2026-08-11** trên chính repo này: SDK `8.0.407`, publish self-contained
> `win-x64` ra **608 file / 142 MB**, chạy file `.exe` đã publish ở chế độ Production →
> `/health/live` trả **200**, `/health/ready` trả **Healthy**, giao diện web trả **200**, thư mục dữ
> liệu tự tạo `logs/`, `audit/`, `mqtt/spool/`. Các con số và lệnh dưới đây lấy từ lần chạy đó.

Mục lục:

1. [Phạm vi và yêu cầu](#1-phạm-vi-và-yêu-cầu)
2. [Chuẩn bị máy build](#2-chuẩn-bị-máy-build)
3. [Kiểm tra trước khi publish](#3-kiểm-tra-trước-khi-publish)
4. [Publish phần mềm](#4-publish-phần-mềm)
5. [Chuẩn bị IPC](#5-chuẩn-bị-ipc)
6. [Chép sang IPC và bố trí thư mục](#6-chép-sang-ipc-và-bố-trí-thư-mục)
7. [Cấu hình production](#7-cấu-hình-production)
8. [Cài đặt Windows service](#8-cài-đặt-windows-service)
9. [Nạp cấu hình dự án](#9-nạp-cấu-hình-dự-án)
10. [Checklist nghiệm thu](#10-checklist-nghiệm-thu)
11. [Vận hành và sao lưu](#11-vận-hành-và-sao-lưu)
12. [Nâng cấp phiên bản và rollback](#12-nâng-cấp-phiên-bản-và-rollback)
13. [Gỡ cài đặt](#13-gỡ-cài-đặt)
14. [Xử lý sự cố](#14-xử-lý-sự-cố)
15. [Phụ lục](#15-phụ-lục)

---

## 1. Phạm vi và yêu cầu

Cái được triển khai là **một tiến trình duy nhất**: `HinoGateway.Server.exe`. Nó đã chứa sẵn REST API,
SignalR, giao diện Blazor WebAssembly, engine hỏi thiết bị, alarm, IEC-104 và MQTT. **Không có
database, không có IIS, không có Docker** — chép một thư mục sang máy là chạy được.

| Hạng mục | Yêu cầu |
| --- | --- |
| Máy build | Windows + .NET SDK 8.0.4xx (`global.json` ghim `8.0.407`, `rollForward: latestFeature`) |
| IPC | Windows 10/11 hoặc Windows Server, x64. Tối thiểu 2 core / 4 GB RAM |
| Dung lượng trên IPC | ~200 MB cho chương trình + chỗ cho log/audit (mặc định audit giới hạn 2 GiB) |
| .NET trên IPC | **Không cần** nếu publish self-contained (mục 4.1). Cần ASP.NET Core Runtime 8 nếu chọn framework-dependent (mục 4.2) |
| Quyền | Tài khoản Administrator trên IPC để tạo service và cấp quyền thư mục dữ liệu |
| Mạng | Cổng HTTP của gateway (mặc định 2000), cổng IEC-104 (2404) nếu dùng, cổng đến PLC (Modbus 502 / S7 102), cổng broker MQTT |

Phiên bản định dạng dự án hiện tại là **version 7**. Nếu nâng cấp từ bản cũ hơn, đọc
`RELEASE_NOTES_2026-08-11.md` trước — nó đổi một số mặc định vận hành (thư mục dữ liệu, SBO cho lệnh
IEC, hành vi khi audit lỗi).

---

## 2. Chuẩn bị máy build

```powershell
dotnet --version          # phải là 8.0.4xx
dotnet --list-runtimes    # tham khảo
git -C C:\...\HinoGateway status
```

Nếu `dotnet --version` báo không tìm thấy SDK khớp `global.json`: cài .NET SDK 8.0.4xx từ
<https://dotnet.microsoft.com/download/dotnet/8.0>.

**Luôn build từ một cây mã nguồn sạch, đã commit.** Sau này khi có sự cố ngoài hiện trường, thứ duy
nhất truy được về bản đang chạy là commit đã build ra nó.

```powershell
git -C C:\...\HinoGateway rev-parse --short HEAD    # ghi lại mã commit vào biên bản bàn giao
```

---

## 3. Kiểm tra trước khi publish

Không publish một cây mã chưa chạy qua test. Chạy tuần tự (các cờ `-m:1 -nr:false` giữ MSBuild chạy
một tiến trình, tránh khoá file trên máy cấu hình yếu):

```powershell
cd C:\...\HinoGateway
dotnet restore HinoGateway.sln -m:1 -nr:false -p:RestoreDisableParallel=true
dotnet build HinoGateway.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test tests/HinoGateway.Engine.Tests/HinoGateway.Engine.Tests.csproj -c Release --no-build -m:1 -nr:false
```

Trước một đợt bàn giao thật, chạy thêm tải trên **chính phần cứng IPC sẽ dùng** (hoặc máy tương
đương):

```powershell
dotnet run -c Release --project tests/HinoGateway.LoadTests -- quick
dotnet run -c Release --project tests/HinoGateway.LoadTests -- soak     # ~30 phút
```

`soak` là thứ phát hiện rò rỉ bộ nhớ và trôi nhịp poll — thứ mà test đơn vị không thấy. Báo cáo in ra
tỉ lệ poll, drop hàng đợi, CPU/RSS, heap và phân mảnh.

Điều kiện để đi tiếp: **build xanh, toàn bộ test PASS, `soak` không tăng heap đều đặn.**

---

## 4. Publish phần mềm

Có hai kiểu publish. **Với IPC ngoài hiện trường, dùng 4.1 (self-contained).**

### 4.1 Self-contained win-x64 — khuyến nghị

Gói kèm luôn .NET runtime. IPC không cần cài gì, và việc ai đó cập nhật/gỡ .NET trên IPC không làm
chết gateway.

```powershell
cd C:\...\HinoGateway
dotnet publish src/HinoGateway.Server/HinoGateway.Server.csproj `
  -c Release -r win-x64 --self-contained true `
  -o C:\Build\HinoGateway-publish `
  -m:1 -nr:false -p:UseSharedCompilation=false
```

Kết quả thực tế đo được: **608 file, 142 MB**, trong đó có `HinoGateway.Server.exe` (khởi động trực
tiếp, không cần `dotnet`), `wwwroot/` chứa giao diện Blazor WASM, và `appsettings.json`.

Thư mục đích **phải nằm ngoài cây mã nguồn**. Xem cảnh báo ở 4.3.

### 4.2 Framework-dependent — chỉ khi bạn quản được runtime trên IPC

Nhỏ hơn nhiều (~vài chục MB) nhưng IPC **phải** có sẵn **ASP.NET Core Runtime 8** (bản Hosting
Bundle hoặc `dotnet-runtime` + `aspnetcore-runtime`).

```powershell
dotnet publish src/HinoGateway.Server/HinoGateway.Server.csproj `
  -c Release -r win-x64 --self-contained false `
  -o C:\Build\HinoGateway-publish-fdd -m:1 -nr:false
```

Kiểm tra trên IPC trước: `dotnet --list-runtimes` phải có dòng `Microsoft.AspNetCore.App 8.x`.

### 4.3 Publish từ Visual Studio — đọc trước khi bấm

Profile có sẵn: `src/HinoGateway.Server/Properties/PublishProfiles/FolderProfile.pubxml`
(`win-x64`, `SelfContained=true`, `DeleteExistingFiles=false`).

> ⚠ **`DeleteExistingFiles` phải luôn là `false`, và thư mục đích không bao giờ được trỏ vào (hoặc
> lên trên) thư mục mã nguồn.** Ngày 2026-08-10 một lần publish với thư mục đích sai đã **xoá sạch
> cây mã nguồn** của dự án này. Đây không phải cảnh báo lý thuyết. Nếu đổi `PublishUrl`, đọc lại giá
> trị đó hai lần trước khi bấm Publish.

Khuyến nghị: dùng dòng lệnh ở 4.1 cho bản giao hàng; Visual Studio chỉ dùng khi thử nghiệm.

### 4.4 Kiểm tra thư mục publish trước khi mang đi

Thư mục publish **phải có**:

- `HinoGateway.Server.exe`, `HinoGateway.Server.dll`, `HinoGateway.Server.runtimeconfig.json`
- `appsettings.json`
- `wwwroot/` (có `index.html`, `_framework/`)
- các `HinoGateway.*.dll` (Core, Engine, Storage, Expressions, Web, Drivers.Modbus,
  Drivers.SiemensS7, Drivers.Simulator, Northbound.Iec104, Northbound.Mqtt)

Thư mục publish **không được có** (nếu có thì xoá đi trước khi chép):

| File | Vì sao |
| --- | --- |
| `hino.json`, `hino.json.bak` | Cấu hình dự án phải nằm ở thư mục dữ liệu, không nằm cạnh binary. Có hai bản sẽ trôi lệch nhau. Project file đã cố tình loại nó khỏi output |
| `hino.secrets.json` | Chứa bí mật của máy dev |
| `appsettings.Development.json` | Vô hại vì Production không đọc, nhưng xoá cho gọn |
| `hino.db*`, `*.superseded` | Rác trong thư mục dev, không thuộc phần mềm |

File `.pdb` (11 file) **nên giữ lại**: khi có exception ngoài hiện trường, chúng cho số dòng trong
stack trace.

Đóng gói và ghi mã băm để biết chắc bản trên IPC đúng là bản đã test:

```powershell
$stamp  = Get-Date -Format yyyyMMdd-HHmm
$commit = (git -C C:\...\HinoGateway rev-parse --short HEAD)
$zip    = "C:\Build\HinoGateway-$stamp-$commit.zip"

Compress-Archive -Path C:\Build\HinoGateway-publish\* -DestinationPath $zip
Get-FileHash $zip -Algorithm SHA256 | Format-List
Write-Host "File cần mang sang IPC: $zip"
```

**Ghi lại tên file thật vừa in ra** — các mục sau dùng nó. Tên có dấu thời gian và mã commit nên mỗi
lần build là một tên khác; đừng gõ lại theo ví dụ trong tài liệu.

---

## 5. Chuẩn bị IPC

1. **Đồng hồ hệ thống**: đặt đúng giờ và đúng múi giờ, bật đồng bộ NTP. Nhãn thời gian của tag, alarm
   và audit lấy từ đồng hồ máy; sai giờ là hỏng toàn bộ dữ liệu lịch sử phía trên.
2. **Nguồn điện / tự khởi động lại**: bật auto-login không cần thiết (gateway chạy như service),
   nhưng phải bảo đảm máy tự bật lại sau mất điện (thiết lập trong BIOS: *Restore on AC Power Loss*).
3. **Windows Update**: đặt giờ khởi động lại ngoài giờ vận hành, hoặc hoãn.
4. **Chống ghi / Unified Write Filter**: nếu IPC bật UWF, thư mục dữ liệu (`%ProgramData%\HinoGateway`)
   **phải** được đưa vào danh sách loại trừ, nếu không cấu hình và audit mất sau mỗi lần khởi động.
5. **Phần mềm diệt virus**: loại trừ thư mục chương trình và thư mục dữ liệu. Quét thời gian thực
   trên file audit JSONL đang ghi liên tục sẽ làm tăng độ trễ.
6. **Tường lửa** — mở đúng những gì cần, không hơn:

```powershell
# Giao diện web + API (chỉ mở nếu cần truy cập từ máy khác)
New-NetFirewallRule -DisplayName "HinoGateway HTTP" -Direction Inbound -Protocol TCP -LocalPort 2000 -Action Allow
# IEC-104 (trung tâm điều độ kết nối vào) — nên giới hạn địa chỉ nguồn
New-NetFirewallRule -DisplayName "HinoGateway IEC-104" -Direction Inbound -Protocol TCP -LocalPort 2404 `
  -RemoteAddress 10.0.0.0/24 -Action Allow
```

IEC-104 **không có xác thực** ở mức giao thức. Bắt buộc dùng đồng thời: `AllowedClients` (CIDR) trong
cấu hình gateway **và** luật tường lửa. Nếu đường truyền đi qua hạ tầng không tin cậy, phải bọc
VPN/TLS.

---

## 6. Chép sang IPC và bố trí thư mục

Quy ước thư mục (giữ nguyên trên mọi trạm để hỗ trợ từ xa cho dễ):

| Thư mục | Nội dung | Quyền |
| --- | --- | --- |
| `C:\Program Files\HinoGateway` | Binary đã publish. **Chỉ đọc** với tài khoản chạy service | Administrators: Full; tài khoản service: Read & Execute |
| `C:\ProgramData\HinoGateway` | Toàn bộ trạng thái: `hino.json`, `hino.json.bak`, `hino.secrets.json`, `hino.retain.json`, `logs/`, `audit/`, `mqtt/spool/` | Tài khoản service: **Full control** |

Đây là mặc định của phần mềm khi chạy Production trên Windows — không cần cấu hình gì nếu dùng đúng
hai đường dẫn này.

### 6.1 Chép file zip sang IPC

Bước này phải làm **trước**, bằng USB, ổ mạng, hoặc kéo-thả qua Remote Desktop. Nếu nối được từ máy
build sang IPC bằng PowerShell Remoting:

```powershell
# Chạy trên máy build
$s = New-PSSession -ComputerName <ip-ipc> -Credential (Get-Credential)
New-Item -ItemType Directory -Force C:\Temp | Out-Null
Copy-Item $zip -Destination C:\Temp\ -ToSession $s
Remove-PSSession $s
```

### 6.2 Bung ra thư mục chương trình

```powershell
# Trên IPC, PowerShell nâng quyền (Run as Administrator)

# Xác định tên file thật, KHÔNG gõ theo ví dụ trong tài liệu
$zip = (Get-ChildItem C:\Temp\HinoGateway-*.zip | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
if (-not $zip) { throw "Chưa có file zip trong C:\Temp — quay lại mục 6.1." }
Write-Host "Sẽ cài đặt từ: $zip"

New-Item -ItemType Directory -Force 'C:\Program Files\HinoGateway' | Out-Null
Expand-Archive -Path $zip -DestinationPath 'C:\Program Files\HinoGateway' -Force
New-Item -ItemType Directory -Force 'C:\ProgramData\HinoGateway' | Out-Null

# Kiểm tra ngay: file exe phải nằm TRỰC TIẾP trong thư mục chương trình
Test-Path 'C:\Program Files\HinoGateway\HinoGateway.Server.exe'   # phải trả về True
```

Nếu `Test-Path` trả về `False`, gần như chắc chắn zip được nén cả thư mục cha nên nội dung nằm sâu
thêm một cấp (`...\HinoGateway\HinoGateway-publish\HinoGateway.Server.exe`). Cách nén ở mục 4.4 dùng
`\*` chính là để tránh chuyện này; nếu đã lỡ, chỉ cần dời nội dung lên một cấp.

**Không dùng zip cũng được** — chép thẳng thư mục publish nếu hai máy nhìn thấy nhau qua mạng:

```powershell
robocopy '\\<máy-build>\Build\HinoGateway-publish' 'C:\Program Files\HinoGateway' /MIR /R:2 /W:2
```

> **Không bao giờ để gateway ghi vào thư mục chương trình.** Tách chương trình và dữ liệu là thứ làm
> cho việc nâng cấp (mục 12) trở thành "chép đè thư mục chương trình" thay vì một cuộc di trú.

---

## 7. Cấu hình production

### 7.1 Đặt cấu hình ở đâu

Ba cách, ưu tiên tăng dần (sau đè lên trước):

1. `appsettings.json` trong thư mục chương trình — **không sửa file này**, nó bị ghi đè mỗi lần nâng cấp.
2. `appsettings.Production.json` đặt cạnh `.exe` — dùng cho cấu hình cố định của trạm.
3. **Biến môi trường** — ưu tiên cao nhất, và là chỗ duy nhất nên để bí mật (API key, mật khẩu).

Biến môi trường dùng `__` (hai gạch dưới) thay cho dấu `:` phân cấp:

```text
Data__Root=D:\HinoGatewayData
Kestrel__Endpoints__Http__Url=http://0.0.0.0:2000
Security__Enabled=true
```

Với một service, đặt biến ở mức máy để service đọc được:

```powershell
[Environment]::SetEnvironmentVariable('Data__Root','D:\HinoGatewayData','Machine')
```

### 7.2 Các khoá cần biết

| Khoá | Mặc định | Ý nghĩa |
| --- | --- | --- |
| `Data:Root` | `%ProgramData%\HinoGateway` (Windows/Production) | Thư mục trạng thái. **Bắt buộc là đường dẫn tuyệt đối** — đường dẫn tương đối làm gateway từ chối khởi động, vì nó sẽ phụ thuộc vào thư mục làm việc của tiến trình |
| `Data:SeedDemo` | `false` | Production **không** tự tạo thiết bị demo khi thiếu `hino.json`; nó khởi động rỗng và ghi log cảnh báo |
| `Kestrel:Endpoints:Http:Url` | `http://localhost:2000` | **Mặc định chỉ nghe loopback.** Muốn truy cập từ máy khác phải đổi thành `http://0.0.0.0:2000` hoặc IP cụ thể |
| `Security:Enabled` | `false` | Bật xác thực bằng API key |
| `Security:RequireHttps` | `true` | Khi bật security mà không có endpoint HTTPS, gateway **từ chối khởi động** |
| `Security:ApiKeys` | `[]` | Danh sách `{ Name, Key, Role }`, `Role` ∈ `Viewer` / `Operator` / `Admin` |
| `Security:AllowLocalConsole` | `true` | Khi security **tắt**: request từ chính máy đó được quyền Admin, request từ nơi khác chỉ được `Viewer` |
| `Commands:*` | 20/s, hàng đợi 256, 8 worker, timeout 10 s | Điều tiết lệnh điều khiển xuống thiết bị |
| `Audit:*` | 90 ngày / 180 file / 2 GiB | Lưu giữ vết audit |
| `Serilog:File:*` | 14 file / 32 MB | Xoay vòng log |

### 7.3 Bảo mật

Đây là quyết định vận hành, không phải tuỳ chọn kỹ thuật. Chọn một trong hai:

**A. Trạm kín, chỉ thao tác tại chỗ** (đơn giản nhất, an toàn nếu mạng thực sự cô lập):

- `Security:Enabled = false`
- Giữ `Kestrel` nghe `http://localhost:2000` (mặc định)
- Người ngồi tại IPC có quyền Admin; không ai từ mạng vào được

**B. Truy cập qua mạng** — bắt buộc bật security:

```jsonc
// C:\Program Files\HinoGateway\appsettings.Production.json
{
  "Kestrel": {
    "Endpoints": {
      "Https": {
        "Url": "https://0.0.0.0:2443",
        "Certificate": { "Path": "C:\\ProgramData\\HinoGateway\\cert.pfx", "Password": "..." }
      }
    }
  },
  "Security": {
    "Enabled": true,
    "RequireHttps": true,
    "ApiKeys": [
      { "Name": "hmi",      "Key": "<sinh ngẫu nhiên>", "Role": "Operator" },
      { "Name": "engineer", "Key": "<sinh ngẫu nhiên>", "Role": "Admin" }
    ]
  }
}
```

Sinh khoá:

```powershell
[Convert]::ToBase64String((1..32 | ForEach-Object { Get-Random -Max 256 }))
```

Ba điểm cần nhớ:

- Client gửi khoá qua header `X-Api-Key`.
- WebSocket của SignalR **không đặt được header tuỳ ý**, nên trình duyệt truyền khoá qua tham số
  `access_token` trên URL. Vì thế: cấu hình proxy/IIS/nginx ghi log **không kèm query string**
  (nginx dùng `$uri` chứ không phải `$request_uri`), coi log proxy như thông tin mật, và đổi khoá
  nếu log có query đã bị lộ.
- `RequireHttps=false` chỉ chấp nhận được khi có reverse proxy tin cậy đã kết thúc TLS ngay trước
  gateway.

---

## 8. Cài đặt Windows service

Ứng dụng đã tích hợp sẵn `AddWindowsService` (tên service `HinoGateway`) — không cần NSSM hay wrapper.

> Quy trình cài đặt đã được chạy thực tế và tách thành hai tài liệu riêng, dùng chúng khi đi lắp đặt:
> [`IPC_CAI_DAT_NHANH.md`](IPC_CAI_DAT_NHANH.md) (script dán-và-chạy) và
> [`IPC_CAI_DAT_CHI_TIET.md`](IPC_CAI_DAT_CHI_TIET.md) (từng bước kèm cách kiểm tra và các chỗ dễ vấp).

### 8.1 Cách nhanh: chạy dưới `LocalSystem`

```powershell
New-Service -Name HinoGateway `
  -BinaryPathName '"C:\Program Files\HinoGateway\HinoGateway.Server.exe"' `
  -DisplayName 'HinoGateway' -StartupType Automatic
sc.exe description HinoGateway "Industrial edge gateway - Modbus/S7 to IEC-104/MQTT"
Start-Service HinoGateway
Get-Service HinoGateway
```

### 8.2 Cách nên dùng: tài khoản riêng, ít quyền

Tài khoản quy ước là `HinoInstaller`. Xem `IPC_CAI_DAT_CHI_TIET.md` bước 4–6 cho bản đầy đủ; tóm tắt:

```powershell
$acct = 'HinoInstaller'
$pw   = Read-Host -AsSecureString "Mật khẩu cho tài khoản $acct"

# 1. Tạo tài khoản cục bộ (KHÔNG thêm vào nhóm Administrators)
New-LocalUser -Name $acct -Password $pw -PasswordNeverExpires -AccountNeverExpires `
  -UserMayNotChangePassword -Description "HinoGateway service account"

# 2. Cấp quyền "Log on as a service" — New-Service KHÔNG tự cấp, thiếu là lỗi 1069
#    (khối secedit đầy đủ nằm ở IPC_CAI_DAT_CHI_TIET.md bước 4b)

# 3. Cấp FULL CONTROL thư mục dữ liệu (xem ghi chú bên dưới — Modify là chưa đủ)
icacls 'C:\ProgramData\HinoGateway' /grant "${acct}:(OI)(CI)F"

# 4. Thư mục chương trình chỉ cho đọc
icacls 'C:\Program Files\HinoGateway' /grant "${acct}:(OI)(CI)RX"

# 5. Tạo service với tài khoản đó
$cred = New-Object System.Management.Automation.PSCredential("$env:COMPUTERNAME\$acct", $pw)
New-Service -Name HinoGateway `
  -BinaryPathName '"C:\Program Files\HinoGateway\HinoGateway.Server.exe"' `
  -StartupType Automatic -Credential $cred
Start-Service HinoGateway

# 6. Kiểm tra service THẬT SỰ chạy bằng tài khoản đó
Get-CimInstance Win32_Service -Filter "Name='HinoGateway'" | Select-Object State, StartName
```

> **Vì sao phải Full control chứ không phải Modify:** khi khởi động, gateway ghi lại ACL của thư mục
> dữ liệu để chỉ còn chính nó, `SYSTEM` và `Administrators` — thao tác này cần quyền *Change
> permissions*, mà "Modify" không bao gồm. Nếu chỉ có Modify, gateway **vẫn chạy** nhưng ghi một dòng
> WARNING và để nguyên quyền thừa kế. Nó cố tình không tự sát vì lý do này: một gateway chạy được với
> ACL lỏng còn hơn một gateway không lên được trên máy không có người trực.

> **Ba cái bẫy đã gặp thật khi cài, đều làm mọi thứ trông như đã thành công:**
>
> 1. `Get-Credential` **từ chối định dạng `.\tài-khoản`** và bung tooltip "Examples" — trông như báo
>    sai mật khẩu trong khi mật khẩu chưa hề được kiểm tra. Dựng `PSCredential` bằng lệnh như trên.
> 2. Nếu service đã tồn tại, `New-Service` báo `The specified service already exists` và **`-Credential`
>    không được áp dụng** — service vẫn chạy `LocalSystem`, trong khi `sc.exe` phía sau vẫn SUCCESS và
>    `Get-Service` vẫn `Running`. Vì thế bước 6 ở trên là bắt buộc, không phải tuỳ chọn.
> 3. Mỗi lần khởi động, gateway ghi lại ACL thành danh sách **đóng**. Nếu nó từng chạy dưới
>    `LocalSystem`, ACE của tài khoản riêng **đã bị xoá**. Khi chuyển service sang tài khoản riêng,
>    phải `icacls ... /grant` **lại** trước khi chạy, theo thứ tự: dừng service → cấp quyền →
>    `sc.exe config obj=` → chạy.

### 8.3 Bắt Windows tự khởi động lại khi tiến trình chết

Khi một device worker bị bỏ rơi, supervisor **thoát tiến trình với exit code 2** thay vì tạo worker
trùng lặp — đúng để Service Control Manager khởi động lại một tiến trình sạch. Phải khai báo hành vi
này, nếu không service sẽ nằm im ở trạng thái Stopped:

```powershell
sc.exe failure HinoGateway reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe failureflag HinoGateway 1
```

### 8.4 Chạy thử bằng tay trước khi cài service (khuyến khích)

```powershell
cd 'C:\Program Files\HinoGateway'
$env:DOTNET_ENVIRONMENT = 'Production'
.\HinoGateway.Server.exe
```

Log ra console. Đọc 15 dòng đầu — chúng nói đúng những gì cần biết: thư mục dữ liệu thật sự đang
dùng, có tìm thấy `hino.json` không, số connection/alarm/internal tag đã nạp, IEC-104 bật hay tắt, và
địa chỉ đang lắng nghe. Ctrl+C để dừng, rồi mới cài service.

---

## 9. Nạp cấu hình dự án

Production khởi động **rỗng** khi chưa có `hino.json` — đây là chủ ý, để một thay đổi ngoài ý muốn về
`Data:Root` không âm thầm chạy một nhà máy giả lập. Nạp cấu hình bằng một trong hai cách:

**Cách 1 — qua giao diện:** mở `http://<ip-ipc>:2000`, vào trang **Connections**, dùng chức năng
import file `.hino`.

**Cách 2 — qua API:**

```powershell
# Xuất từ trạm nguồn / trạm staging
Invoke-WebRequest 'http://<nguồn>:2000/api/config/export' -OutFile C:\Temp\dự-án.hino

# Nạp vào IPC. replace=true = khôi phục toàn bộ (xoá cấu hình cũ); bỏ đi = trộn thêm
Invoke-RestMethod 'http://localhost:2000/api/config/import?replace=true' -Method Post `
  -ContentType 'application/json' -InFile C:\Temp\dự-án.hino

# Import KHÔNG tự áp dụng — phải nạp lại engine
Invoke-RestMethod 'http://localhost:2000/api/config/reload' -Method Post
```

Vài điều cần nhớ về file `.hino`:

- Nó **cố tình không chứa bí mật** (mật khẩu MQTT…) và không chứa giá trị retentive. Sau khi import
  phải nhập lại bí mật ở trang MQTT.
- `reload` kiểm tra cấu hình trước; nếu dự án không hợp lệ thì runtime **giữ nguyên** và API trả về
  danh sách lỗi. Một connection sai cấu hình bị từ chối riêng nó, các connection còn lại vẫn chạy —
  áp dụng cấu hình không bao giờ làm gateway ngừng phát.
- File từ phiên bản cũ hơn 7 sẽ được migrate. **Migrate không tự bật `AllowWrite`.** Xem
  `RELEASE_NOTES_2026-08-11.md` mục version 7 trước khi áp dụng lên trạm có điều khiển.

---

## 10. Checklist nghiệm thu

Chạy toàn bộ, ghi kết quả vào biên bản. Thay `<ip>` bằng địa chỉ IPC.

| # | Kiểm tra | Lệnh / thao tác | Đạt khi |
| --- | --- | --- | --- |
| 1 | Service đang chạy | `Get-Service HinoGateway` | `Running`, StartType `Automatic` |
| 2 | Liveness | `curl http://<ip>:2000/health/live` | HTTP **200** |
| 3 | Readiness | `curl http://<ip>:2000/health/ready` | **`Healthy`** |
| 4 | Thành phần nền | `curl http://<ip>:2000/api/health/components` | Không có mục nào ở trạng thái stopped/stale/crash-loop |
| 5 | Giao diện | Mở `http://<ip>:2000` trên trình duyệt | Trang Live Data hiện, có dữ liệu chạy |
| 6 | Các trang | `/connections`, `/tags`, `/alarms`, `/alarm-rules`, `/iec104`, `/mqtt`, `/access` | Mở được, không lỗi |
| 7 | Thiết bị | Trang Live Data / `curl .../api/health` | Mọi connection `Connected`, chất lượng tag `Good` |
| 8 | Thư mục dữ liệu | `dir C:\ProgramData\HinoGateway` | Có `hino.json`, `logs\`, `audit\`, `mqtt\spool\` |
| 9 | Log | `Get-Content C:\ProgramData\HinoGateway\logs\hinogateway-*.log -Tail 40` | Không có ERROR/FATAL lặp lại |
| 10 | Cảnh báo quyền | Tìm chuỗi `Could not restrict permissions` trong log | **Không xuất hiện** (nếu có: xem mục 8.2) |
| 11 | IEC-104 | Trang `/iec104` + trung tâm điều độ | Trạm chạy, client đã kết nối, không tăng bộ đếm drop |
| 12 | MQTT | Trang `/mqtt` | Đã kết nối broker, spool không tăng liên tục |
| 13 | Audit | `dir C:\ProgramData\HinoGateway\audit` | Có file `.jsonl` sau khi thực hiện một lệnh thử |
| 14 | **Khởi động lại máy** | `Restart-Computer`, chờ, làm lại bước 1–7 | Gateway tự lên, dữ liệu và cấu hình còn nguyên |
| 15 | Tự phục hồi | `Stop-Process -Name HinoGateway.Server -Force` | Service tự chạy lại trong ~5 s (nhờ mục 8.3) |
| 16 | Bảo mật (nếu bật) | Gọi API **không** kèm `X-Api-Key` | Bị từ chối / chỉ đọc, đúng như thiết kế |
| 17 | Ổn định | Để chạy ≥ 30 phút, xem lại bước 3, 4, 9 | Không suy giảm, RAM không tăng đều |

Bước 14 là bước hay bị bỏ qua nhất và cũng là bước hay lộ lỗi nhất (service chưa Automatic, thư mục
dữ liệu nằm trên ổ mạng chưa mount kịp, UWF nuốt mất cấu hình).

---

## 11. Vận hành và sao lưu

**Nhật ký và audit**

- Log ứng dụng: `C:\ProgramData\HinoGateway\logs\hinogateway-YYYYMMDD.log`, xoay theo ngày và theo
  kích thước (mặc định giữ 14 file, mỗi file 32 MB).
- Vết audit: `C:\ProgramData\HinoGateway\audit\*.jsonl`, ghi theo lô có fsync, giữ 90 ngày / 180 file
  / 2 GiB.
- **Theo dõi dung lượng trống của ổ chứa thư mục dữ liệu.** Nếu audit không ghi được, lệnh xuống
  thiết bị **vẫn được thực hiện** và báo `auditPersisted: false`, đồng thời thành phần `audit-trail`
  chuyển sang unhealthy. Nếu quy định của trạm yêu cầu vết audit đầy đủ thì đây là điều kiện dừng vận
  hành.

**Sao lưu** — sao lưu **toàn bộ thư mục dữ liệu**, không chỉ file `.hino`:

```powershell
Stop-Service HinoGateway          # để có bản chụp nhất quán (có thể bỏ nếu chấp nhận sao lưu nóng)
$stamp = Get-Date -Format yyyyMMdd-HHmm
Compress-Archive -Path C:\ProgramData\HinoGateway\* -DestinationPath "D:\Backup\hino-data-$stamp.zip"
Start-Service HinoGateway
```

Bản export `.hino` là để **di chuyển cấu hình giữa các trạm**, không phải bản sao lưu đầy đủ: nó
không chứa bí mật và không chứa giá trị retentive.

**Điều cần biết về alarm:** trạng thái alarm và nhật ký alarm cục bộ nằm **trong bộ nhớ**. Sau khi
khởi động lại, chúng được dựng lại từ giá trị tag mới. Vết audit **không phải** là historian alarm —
lịch sử alarm dài hạn phải lấy từ SCADA/MQTT phía trên.

---

## 12. Nâng cấp phiên bản và rollback

Nhờ tách chương trình / dữ liệu, nâng cấp chỉ là chép đè thư mục chương trình.

```powershell
# 0. Đọc release notes của bản mới. Nếu đổi phiên bản định dạng dự án: thử trên trạm staging trước.

# 1. Sao lưu
$stamp = Get-Date -Format yyyyMMdd-HHmm
Compress-Archive -Path C:\ProgramData\HinoGateway\* -DestinationPath "D:\Backup\hino-data-$stamp.zip"

# 2. Dừng service
Stop-Service HinoGateway

# 3. Giữ lại bản chương trình cũ để rollback
Rename-Item 'C:\Program Files\HinoGateway' "HinoGateway.old-$stamp"

# 4. Bung bản mới
New-Item -ItemType Directory -Force 'C:\Program Files\HinoGateway'
Expand-Archive -Path C:\Temp\HinoGateway-<bản-mới>.zip -DestinationPath 'C:\Program Files\HinoGateway'

# 5. Chép lại appsettings.Production.json nếu có
Copy-Item "C:\Program Files\HinoGateway.old-$stamp\appsettings.Production.json" `
          'C:\Program Files\HinoGateway\' -ErrorAction SilentlyContinue

# 6. Chạy lại
Start-Service HinoGateway

# 7. Làm lại checklist mục 10 (tối thiểu bước 1–9)
```

**Rollback** khi bản mới hỏng:

```powershell
Stop-Service HinoGateway
Remove-Item 'C:\Program Files\HinoGateway' -Recurse -Force
Rename-Item "C:\Program Files\HinoGateway.old-$stamp" 'HinoGateway'
# Nếu bản mới đã migrate hino.json lên phiên bản cao hơn, khôi phục luôn dữ liệu:
Expand-Archive "D:\Backup\hino-data-$stamp.zip" -DestinationPath C:\ProgramData\HinoGateway -Force
Start-Service HinoGateway
```

Lưu ý: bản cũ **không đọc được** file dự án có version cao hơn nó hỗ trợ — nó sẽ từ chối. Vì thế
bước khôi phục dữ liệu ở trên là bắt buộc nếu đã kịp nâng cấp định dạng dự án.

Chỉ xoá thư mục `.old-*` sau khi bản mới đã chạy ổn định vài ngày.

---

## 13. Gỡ cài đặt

```powershell
Stop-Service HinoGateway
sc.exe delete HinoGateway
Remove-Item 'C:\Program Files\HinoGateway' -Recurse -Force
Remove-NetFirewallRule -DisplayName 'HinoGateway*'
# Dữ liệu: sao lưu TRƯỚC khi xoá — trong này có cấu hình và vết audit
# Remove-Item 'C:\ProgramData\HinoGateway' -Recurse -Force
```

---

## 14. Xử lý sự cố

| Hiện tượng | Nguyên nhân | Cách xử lý |
| --- | --- | --- |
| `Expand-Archive : The path '...zip' either does not exist or is not a valid file system path` | File zip chưa được tạo ở mục 4.4, hoặc chưa chép sang IPC, hoặc tên file khác với ví dụ trong tài liệu | `Get-ChildItem C:\Temp\*.zip` để xem thực tế có gì. Dùng đoạn lệnh ở mục 6.2 (tự dò tên file mới nhất) thay vì gõ tên tay. Hai thư mục đã tạo trước đó vẫn dùng lại được, không cần xoá |
| Bung zip xong nhưng không thấy `HinoGateway.Server.exe` ở gốc thư mục chương trình | Zip được nén kèm thư mục cha | Dời nội dung lên một cấp, hoặc nén lại theo đúng mục 4.4 (`-Path ...\HinoGateway-publish\*`) |
| Service Start rồi Stop ngay | Cấu hình sai làm ứng dụng ném lỗi khi khởi động | Chạy tay `.\HinoGateway.Server.exe` (mục 8.4) để thấy thông báo lỗi thật; xem thêm `logs\` |
| `Data:Root must be an absolute path` | Đặt `Data:Root` bằng đường dẫn tương đối | Đổi thành đường dẫn tuyệt đối, ví dụ `D:\HinoGatewayData` |
| `Security is enabled but no valid Security:ApiKeys` | Bật security mà chưa khai khoá | Khai `Security:ApiKeys` (mục 7.3) |
| `Security is enabled with RequireHttps=true, but no HTTPS endpoint` | Bật security nhưng chỉ có endpoint HTTP | Cấu hình `Kestrel:Endpoints:Https` kèm chứng thư, **hoặc** đặt `RequireHttps=false` chỉ khi có reverse proxy tin cậy kết thúc TLS |
| Log có `Could not restrict permissions on the data root` | Tài khoản service chỉ có Modify, không có Full control | `icacls 'C:\ProgramData\HinoGateway' /grant 'svcHino:(OI)(CI)F'` rồi khởi động lại service |
| Máy khác không mở được giao diện | Kestrel mặc định chỉ nghe `localhost` | Đặt `Kestrel__Endpoints__Http__Url=http://0.0.0.0:2000` **và** mở tường lửa |
| `Failed to bind to address ... address already in use` | Cổng 2000 đã bị chiếm | `netstat -ano \| findstr :2000` để tìm PID, đổi cổng hoặc dừng tiến trình kia |
| Giao diện trắng, hoặc F12 báo lỗi tải `_framework/*` | Thiếu `wwwroot/` trong bản publish | Publish lại; kiểm tra theo mục 4.4 |
| Gateway khởi động "rỗng", không thấy thiết bị | Không có `hino.json` trong thư mục dữ liệu, hoặc `Data:Root` trỏ sai chỗ | Đọc dòng log `Gateway data root: ...` để biết nó đang dùng thư mục nào; import lại dự án (mục 9) |
| `Project version N is newer than this server supports` | File `.hino` được tạo bởi bản mới hơn | Nâng cấp gateway, hoặc export lại từ bản tương ứng |
| API trả 401 | Security bật, thiếu/sai header `X-Api-Key` | Kiểm tra khoá; nhớ rằng khoá phân vai trò Viewer/Operator/Admin |
| Ghi tag bị từ chối dù đã đăng nhập Admin | `AllowWrite` của tag đang tắt (mặc định), hoặc vượt giới hạn min/max | Bật `AllowWrite` cho đúng tag ở trang Tags, đặt giới hạn kỹ thuật |
| Lệnh IEC-104 không có tác dụng | Thiếu `AllowCommands` ở trạm, IP nguồn không nằm trong CIDR, hoặc chưa làm SELECT trước EXECUTE (SBO) | Kiểm tra trang `/iec104`; SBO yêu cầu SELECT rồi EXECUTE trong 30 giây |
| MQTT không gửi, spool phình to | Mất kết nối broker, sai thông tin đăng nhập, hoặc broker từ chối PUBLISH | Trang `/mqtt` xem `LastError`; kiểm tra bí mật đã nhập lại sau khi import chưa |
| Thành phần `audit-trail` unhealthy | Ổ đĩa đầy hoặc thư mục dữ liệu không ghi được | Giải phóng dung lượng; kiểm tra quyền. Lệnh vẫn xuống thiết bị nhưng không được ghi vết |
| Service dừng hẳn sau một sự cố, không tự lên lại | Chưa cấu hình `sc failure` | Chạy lệnh ở mục 8.3 (tiến trình thoát với exit code 2 khi cần khởi động lại) |
| Cấu hình biến mất sau khi khởi động lại IPC | Unified Write Filter đang bật | Loại trừ `C:\ProgramData\HinoGateway` khỏi UWF |
| Nhãn thời gian sai | Đồng hồ/múi giờ IPC sai | Đặt lại giờ, bật NTP, khởi động lại service |

**Bộ lệnh chẩn đoán nhanh** (dán cả khối để lấy hiện trạng):

```powershell
Get-Service HinoGateway | Format-List Name,Status,StartType
curl.exe -s -o NUL -w "live=%{http_code}\n" http://localhost:2000/health/live
curl.exe -s http://localhost:2000/health/ready
curl.exe -s http://localhost:2000/api/health/components
curl.exe -s http://localhost:2000/api/stats
Get-Content C:\ProgramData\HinoGateway\logs\hinogateway-*.log -Tail 60
Get-ChildItem C:\ProgramData\HinoGateway
```

---

## 15. Phụ lục

### 15.1 Script publish một bước (chạy trên máy build)

```powershell
param(
  [string]$Out  = "C:\Build\HinoGateway-publish",
  [string]$Repo = "C:\...\HinoGateway"
)
$ErrorActionPreference = 'Stop'

if ($Out -like "$Repo*") { throw "Thư mục publish không được nằm trong cây mã nguồn." }

Set-Location $Repo
$commit = (git rev-parse --short HEAD)

dotnet restore HinoGateway.sln -m:1 -nr:false -p:RestoreDisableParallel=true
dotnet build   HinoGateway.sln -c Release --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet test    tests/HinoGateway.Engine.Tests/HinoGateway.Engine.Tests.csproj -c Release --no-build -m:1 -nr:false

if (Test-Path $Out) { Remove-Item $Out -Recurse -Force }
dotnet publish src/HinoGateway.Server/HinoGateway.Server.csproj `
  -c Release -r win-x64 --self-contained true -o $Out -m:1 -nr:false -p:UseSharedCompilation=false

# Dọn những thứ không được mang ra hiện trường
Remove-Item "$Out\appsettings.Development.json","$Out\hino.json","$Out\hino.secrets.json" -ErrorAction SilentlyContinue

$stamp = Get-Date -Format yyyyMMdd-HHmm
$zip   = "C:\Build\HinoGateway-$stamp-$commit.zip"
Compress-Archive -Path "$Out\*" -DestinationPath $zip
Get-FileHash $zip -Algorithm SHA256 | Format-List
Write-Host "Đã đóng gói commit $commit -> $zip"
```

### 15.2 Script cài đặt trên IPC (PowerShell nâng quyền)

```powershell
param(
  [Parameter(Mandatory)] [string]$Zip,
  [string]$ProgramDir = 'C:\Program Files\HinoGateway',
  [string]$DataDir    = 'C:\ProgramData\HinoGateway',
  [string]$ServiceAccount                                  # bỏ trống = chạy dưới LocalSystem
)
$ErrorActionPreference = 'Stop'

if (Get-Service HinoGateway -ErrorAction SilentlyContinue) { Stop-Service HinoGateway }

New-Item -ItemType Directory -Force $ProgramDir | Out-Null
New-Item -ItemType Directory -Force $DataDir    | Out-Null
Expand-Archive -Path $Zip -DestinationPath $ProgramDir -Force

if ($ServiceAccount) {
  icacls $DataDir    /grant "${ServiceAccount}:(OI)(CI)F"  | Out-Null
  icacls $ProgramDir /grant "${ServiceAccount}:(OI)(CI)RX" | Out-Null
}

if (-not (Get-Service HinoGateway -ErrorAction SilentlyContinue)) {
  $args = @{
    Name           = 'HinoGateway'
    BinaryPathName = "`"$ProgramDir\HinoGateway.Server.exe`""
    DisplayName    = 'HinoGateway'
    StartupType    = 'Automatic'
  }
  if ($ServiceAccount) { $args.Credential = (Get-Credential $ServiceAccount) }
  New-Service @args | Out-Null
  sc.exe failure HinoGateway reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
  sc.exe failureflag HinoGateway 1 | Out-Null
}

Start-Service HinoGateway
Start-Sleep -Seconds 5
Get-Service HinoGateway
curl.exe -s -o NUL -w "live=%{http_code}`n" http://localhost:2000/health/live
```

### 15.3 Bảng biến môi trường thường dùng

| Biến | Ví dụ |
| --- | --- |
| `DOTNET_ENVIRONMENT` | `Production` (mặc định khi không đặt) |
| `Data__Root` | `D:\HinoGatewayData` |
| `Kestrel__Endpoints__Http__Url` | `http://0.0.0.0:2000` |
| `Kestrel__Endpoints__Https__Url` | `https://0.0.0.0:2443` |
| `Security__Enabled` | `true` |
| `Security__ApiKeys__0__Name` / `__Key` / `__Role` | `hmi` / `<khoá>` / `Operator` |
| `Serilog__MinimumLevel__Default` | `Debug` (chỉ khi cần chẩn đoán, nhớ trả lại `Information`) |

### 15.4 Các endpoint hay dùng

| Endpoint | Dùng để |
| --- | --- |
| `GET /health/live` | Tiến trình còn sống (dùng cho giám sát hạ tầng) |
| `GET /health/ready` | Sẵn sàng phục vụ; fail khi có thành phần dừng/treo/khởi động lại liên tục |
| `GET /api/health` | Sức khoẻ từng thiết bị |
| `GET /api/health/components` | Chi tiết thành phần nền |
| `GET /api/stats` | Tổng quan: số thiết bị, chất lượng tag, alarm |
| `GET /api/diagnostics/blocks` | Các khối đọc thực tế trên đường truyền, độ trễ |
| `GET /api/diagnostics/transports` | Các đường truyền dùng chung |
| `GET /api/config/export` | Tải file dự án `.hino` |
| `POST /api/config/import?replace=true` | Nạp dự án (chưa áp dụng) |
| `POST /api/config/reload` | Áp dụng cấu hình đang lưu vào runtime |

### 15.5 Tài liệu liên quan

- `README.md` — chương *Deployment*, an toàn điều khiển, đường lên phía trên
- `RELEASE_NOTES_2026-08-11.md` — thay đổi về mặc định vận hành, **đọc trước khi nâng cấp**
- `REVIEW_2026-08-11_LAN3.md` — kết quả rà soát kỹ thuật gần nhất

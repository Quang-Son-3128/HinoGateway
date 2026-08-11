# HinoGateway — Cài đặt trên IPC, bản chi tiết có kiểm tra

Bản này dùng khi **tự cài và tự nghiệm thu**: mỗi bước nói rõ làm để làm gì, chạy lệnh nào, kết quả
đúng trông như thế nào, và nếu sai thì xử lý ra sao.

Bản rút gọn cho người đi lắp đặt: [`IPC_CAI_DAT_NHANH.md`](IPC_CAI_DAT_NHANH.md).
Tài liệu gốc đầy đủ (publish, nâng cấp, rollback, bảo mật): [`HUONG_DAN_TRIEN_KHAI_IPC.md`](HUONG_DAN_TRIEN_KHAI_IPC.md).

**Quy ước cố định cho mọi trạm** (đã chốt, không đổi giữa các máy):

| Mục | Giá trị |
| --- | --- |
| Thư mục chương trình | `C:\Program Files\HinoGateway` |
| Thư mục dữ liệu | `C:\ProgramData\HinoGateway` |
| Tên service | `HinoGateway` |
| Tài khoản chạy service | `HinoInstaller` — tài khoản **cục bộ**, **không** thuộc nhóm Administrators |
| Mật khẩu | Cố định theo quy định đơn vị, giữ trong trình quản lý mật khẩu. **Không ghi vào tài liệu hay script** |
| Cổng giao diện | `2000` |

Toàn bộ các bước chạy trong **PowerShell mở bằng quyền Administrator**.

---

## Bước 1 — Chép thư mục chương trình và kiểm tra nội dung

**Để làm gì:** đưa bản publish vào đúng chỗ và chứng minh nó đủ file, trước khi dựng service quanh
nó. Một bản publish thiếu `wwwroot` vẫn khởi động được nhưng giao diện sẽ trắng — phát hiện ở đây rẻ
hơn phát hiện sau khi đã cài service.

Chép thư mục publish vào `C:\Program Files\HinoGateway`, rồi:

```powershell
$Src = 'C:\Program Files\HinoGateway'

"exe        : " + (Test-Path "$Src\HinoGateway.Server.exe")
"wwwroot    : " + (Test-Path "$Src\wwwroot\index.html")
"appsettings: " + (Test-Path "$Src\appsettings.json")
"so file    : " + (Get-ChildItem $Src -Recurse -File | Measure-Object).Count
"dung luong : " + ("{0:N0} MB" -f ((Get-ChildItem $Src -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))
```

**Kết quả đúng:** ba dòng đầu `True`, khoảng **608 file**, khoảng **140 MB**.

**Nếu sai:**

- `exe : False` → zip được nén kèm thư mục cha, nội dung nằm sâu thêm một cấp. Dời lên một cấp.
- Chỉ vài chục file / vài chục MB → bản publish là *framework-dependent*, IPC phải cài thêm
  ASP.NET Core Runtime 8. Kiểm tra bằng `dotnet --list-runtimes`.

---

## Bước 2 — Dọn file của máy lập trình, tạo thư mục dữ liệu

**Để làm gì:** thư mục publish có thể lẫn cấu hình và bí mật của máy dev. Nguy hiểm nhất là
`hino.json`: nếu nó nằm cạnh binary, gateway có thể chạy bằng cấu hình của máy lập trình mà **không
báo lỗi gì cả** — nó chạy đúng, chỉ là chạy sai nhà máy.

```powershell
$Src = 'C:\Program Files\HinoGateway'

Remove-Item "$Src\appsettings.Development.json" -Force -ErrorAction SilentlyContinue

"--- file trang thai lot vao (mong doi: khong co gi) ---"
Get-ChildItem $Src -File | Where-Object {
  $_.Name -in 'hino.json','hino.json.bak','hino.secrets.json','hino.retain.json' -or $_.Name -like 'hino.db*'
} | Select-Object Name
"--- het ---"

New-Item -ItemType Directory -Force 'C:\ProgramData\HinoGateway' | Out-Null
"data dir   : " + (Test-Path 'C:\ProgramData\HinoGateway')
"dev config : " + (Test-Path "$Src\appsettings.Development.json")
```

**Kết quả đúng:** giữa hai dòng `---` không có gì; `data dir : True`; `dev config : False`.

**Nếu có file lọt vào:** xoá chúng khỏi thư mục chương trình. Cấu hình thật sẽ nằm ở thư mục dữ liệu,
không nằm cạnh binary.

> Lưu ý: đừng lọc bằng `hino*` — bộ lọc đó khớp luôn cả `HinoGateway.Core.dll` và hàng chục DLL khác,
> làm bạn tưởng có vấn đề trong khi không có.

---

## Bước 3 — Chạy thử bằng tay (chưa cài service)

**Để làm gì:** đây là bước có giá trị chẩn đoán cao nhất trong toàn bộ quy trình. Log hiện thẳng ra
màn hình. Cùng một lỗi đó, khi đã là service, bạn chỉ thấy dòng "Service started and then stopped"
và phải đi mò.

```powershell
cd 'C:\Program Files\HinoGateway'
$env:DOTNET_ENVIRONMENT = 'Production'
.\HinoGateway.Server.exe
```

Chương trình giữ màn hình — đúng, đừng đóng.

**Đọc các dòng log đầu, cần thấy:**

| Dòng | Ý nghĩa |
| --- | --- |
| `Gateway data root: C:\ProgramData\HinoGateway` | Đúng thư mục dữ liệu. Nếu ra `...\AppData\Local\...` là đang chạy nhầm môi trường Development |
| `No project file exists ... starting empty` (WRN) | **Bình thường ở lần chạy đầu.** Production cố tình không tự tạo thiết bị demo, để một thay đổi ngoài ý muốn về thư mục dữ liệu không âm thầm chạy một nhà máy giả |
| `HinoGateway engine started with 0 connection(s)...` | Engine đã lên |
| `Now listening on: http://localhost:2000` | Đã mở cổng |
| `Hosting environment: Production` | Đúng môi trường |

**Không được có:** `Could not restrict permissions on the data root`.

Mở **cửa sổ PowerShell thứ hai** (giữ nguyên cửa sổ đang chạy):

```powershell
curl.exe -s -o NUL -w "live  = %{http_code}`n" http://localhost:2000/health/live
curl.exe -s http://localhost:2000/health/ready
""
curl.exe -s -o NUL -w "`nUI    = %{http_code}`n" http://localhost:2000/
Get-ChildItem C:\ProgramData\HinoGateway
```

**Kết quả đúng:** `live = 200`, `Healthy`, `UI = 200`, và thư mục dữ liệu giờ đã tự sinh ra `logs`,
`audit`, `mqtt`.

Xong thì quay lại cửa sổ 1 bấm **Ctrl+C** để dừng.

---

## Bước 4 — Tạo tài khoản và cấp quyền

**Để làm gì:** gateway không cần quyền admin để chạy. Cho nó một tài khoản riêng, ít quyền, nghĩa là
một lỗ hổng trong gateway không trở thành quyền điều khiển toàn máy.

### 4a. Tạo tài khoản

```powershell
$acct = 'HinoInstaller'
$pw   = Read-Host -AsSecureString "Nhap mat khau cho $acct"

New-LocalUser -Name $acct -Password $pw `
  -PasswordNeverExpires -AccountNeverExpires -UserMayNotChangePassword `
  -Description 'HinoGateway service account'
```

**Kết quả đúng:** in ra một dòng `Name / Enabled / Description`, `Enabled = True`.

> **Chuỗi trong `Read-Host` là *nhãn nhắc*, không phải mật khẩu.** Sau khi Enter, PowerShell in nhãn
> đó ra rồi **đứng chờ bạn gõ mật khẩu** (không hiện ký tự hoặc hiện `*`). Đó không phải treo máy.
>
> **Không thêm tài khoản này vào nhóm Administrators.** Cả điểm của nó là ở chỗ đó.

### 4b. Cấp quyền "Log on as a service"

**Để làm gì:** không có quyền này thì service sẽ báo **lỗi 1069 — logon failure** khi khởi động, và
thông báo đó trông y hệt lỗi sai mật khẩu nên rất dễ đi nhầm hướng. `New-Service` **không** tự cấp
quyền này (chỉ giao diện `services.msc` mới cấp).

```powershell
$sid = (New-Object System.Security.Principal.NTAccount($acct)).Translate([System.Security.Principal.SecurityIdentifier]).Value
$exp = "$env:TEMP\ur-export.inf"; $inf = "$env:TEMP\ur-apply.inf"; $db = "$env:TEMP\ur-apply.sdb"

secedit /export /areas USER_RIGHTS /cfg $exp | Out-Null
$cur  = Get-Content $exp | Where-Object { $_ -match '^SeServiceLogonRight' }
if ($cur) { $list = ($cur -split '=', 2)[1].Trim() } else { $list = '' }

if ($list -notlike "*$sid*") {
  if ($list) { $list = "$list,*$sid" } else { $list = "*$sid" }
  @('[Unicode]','Unicode=yes','[Version]','signature="$CHICAGO$"','Revision=1',
    '[Privilege Rights]', "SeServiceLogonRight = $list") | Set-Content -Path $inf -Encoding Unicode
  secedit /configure /db $db /cfg $inf /areas USER_RIGHTS | Out-Null
  "Da cap 'Log on as a service' cho $acct"
} else {
  "$acct da co quyen 'Log on as a service'"
}
```

**Kết quả đúng:** in ra một trong hai câu thông báo.

Đoạn này cố tình **đọc danh sách hiện có rồi nối thêm SID**, không ghi đè. Ghi đè sẽ tước quyền của
các service khác đang dùng nó.

### 4c. Cấp quyền thư mục

```powershell
icacls 'C:\ProgramData\HinoGateway'  /grant "${acct}:(OI)(CI)F"
icacls 'C:\Program Files\HinoGateway' /grant "${acct}:(OI)(CI)RX"
```

**Kết quả đúng:** cả hai lệnh in `Successfully processed 1 files; Failed processing 0 files`.

> **Vì sao phải Full control chứ không phải Modify:** khi khởi động, gateway **ghi lại ACL** của thư
> mục dữ liệu để khoá nó về đúng ba chủ thể (chính nó, SYSTEM, Administrators). Ghi lại ACL cần quyền
> *Change permissions*, mà "Modify" không bao gồm.
>
> Thiếu nó gateway **vẫn chạy** — nó chỉ ghi một dòng WARNING và để nguyên quyền thừa kế. Thiết kế
> như vậy là cố ý: một gateway chạy được với ACL lỏng và nói ra điều đó vẫn tốt hơn một gateway không
> chịu lên trên máy không có người trực.

### 4d. Kiểm tra

```powershell
Get-LocalUser $acct | Select-Object Name, Enabled
icacls 'C:\ProgramData\HinoGateway'
```

**Kết quả đúng:** `Enabled = True`, và trong danh sách ACL có dòng `...\HinoInstaller:(OI)(CI)(F)`.

---

## Bước 5 — Tạo service

**Để làm gì:** để gateway tự chạy khi máy khởi động, không phụ thuộc vào việc có ai đăng nhập hay
không, và tự dựng lại khi gặp sự cố.

```powershell
$acct = 'HinoInstaller'
$pw   = Read-Host -AsSecureString "Mat khau cua $acct"
$cred = New-Object System.Management.Automation.PSCredential("$env:COMPUTERNAME\$acct", $pw)
$cred.UserName        # phai ra TEN-MAY\HinoInstaller

New-Service -Name HinoGateway `
  -BinaryPathName '"C:\Program Files\HinoGateway\HinoGateway.Server.exe"' `
  -DisplayName 'HinoGateway' -StartupType Automatic -Credential $cred
```

> **Dựng credential bằng lệnh, đừng dùng `Get-Credential`.** Hộp thoại của `Get-Credential` từ chối
> định dạng `.\HinoInstaller` và bung tooltip "Examples" — trông như báo sai mật khẩu trong khi mật
> khẩu còn chưa được kiểm tra lần nào. Dạng `TÊN-MÁY\tài-khoản` thì luôn được chấp nhận.

Khai báo mô tả và **hành vi tự khởi động lại**:

```powershell
sc.exe description HinoGateway "Industrial edge gateway - Modbus/S7 to IEC-104/MQTT"
sc.exe failure     HinoGateway reset= 86400 actions= restart/5000/restart/10000/restart/30000
sc.exe failureflag HinoGateway 1
```

**Kết quả đúng:** ba dòng `[SC] ChangeServiceConfig2 SUCCESS`.

> Ba lệnh này không phải trang trí. Khi một device worker bị bỏ rơi, gateway **cố ý thoát tiến trình
> với exit code 2** để Windows dựng lại một tiến trình sạch, thay vì tạo một worker trùng lặp chạy
> song song. Không khai `sc failure` thì service nằm im ở trạng thái Stopped và không ai biết cho tới
> khi có người mở giao diện.
>
> `failureflag 1` là phần hay bị quên: mặc định Windows chỉ áp dụng hành động phục hồi khi tiến trình
> *crash*, không áp dụng khi tiến trình tự thoát với mã lỗi. Cờ này bắt nó áp dụng cả hai trường hợp.

Chạy:

```powershell
Start-Service HinoGateway
Start-Sleep -Seconds 6
Get-Service HinoGateway | Format-List Name, Status, StartType
```

**Kết quả đúng:** `Status : Running`, `StartType : Automatic`.

---

## Bước 6 — Kiểm tra service chạy đúng tài khoản

**Để làm gì:** đây là chỗ dễ tưởng đã xong mà thật ra chưa. Nếu service đã tồn tại từ trước,
`New-Service` báo `The specified service already exists` và **không áp dụng `-Credential`** — service
vẫn chạy bằng `LocalSystem`. Trong khi đó ba lệnh `sc.exe` phía sau vẫn báo SUCCESS, và
`Get-Service` vẫn báo `Running`. Nhìn qua thì mọi thứ đều xanh.

```powershell
Get-CimInstance Win32_Service -Filter "Name='HinoGateway'" |
  Select-Object Name, State, StartMode, StartName, PathName | Format-List
```

**Kết quả đúng:** `StartName : TÊN-MÁY\HinoInstaller`.

**Nếu `StartName : LocalSystem`**, sửa theo đúng thứ tự sau — **thứ tự quan trọng**:

```powershell
Stop-Service HinoGateway

# 1. Cap lai quyen thu muc TRUOC
icacls 'C:\ProgramData\HinoGateway' /grant "HinoInstaller:(OI)(CI)F"

# 2. Doi tai khoan chay service
if (-not $pw) { $pw = Read-Host -AsSecureString "Mat khau cua HinoInstaller" }
$bstr    = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw)
$pwPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
sc.exe config HinoGateway obj= "$env:COMPUTERNAME\HinoInstaller" password= "$pwPlain"
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $pwPlain = $null

# 3. Chay lai
Start-Service HinoGateway
Start-Sleep -Seconds 6
Get-CimInstance Win32_Service -Filter "Name='HinoGateway'" | Select-Object State, StartName | Format-List
```

> **Vì sao phải cấp lại quyền ở bước 1:** mỗi lần khởi động, gateway ghi lại ACL thư mục dữ liệu
> thành một danh sách **đóng** gồm *chính nó + SYSTEM + Administrators*. Khi nó vừa chạy dưới
> `LocalSystem` thì "chính nó" chính là SYSTEM — và ACE của `HinoInstaller` mà bạn cấp ở bước 4c
> **đã bị dọn đi cùng lúc đó**. Đổi tài khoản mà không cấp lại quyền thì gateway sẽ khởi động với một
> thư mục dữ liệu nó không có quyền ghi.

**Kiểm tra cuối cùng:**

```powershell
curl.exe -s -o NUL -w "live  = %{http_code}`n" http://localhost:2000/health/live
curl.exe -s http://localhost:2000/health/ready
""
"--- log ---"
Get-Content C:\ProgramData\HinoGateway\logs\hinogateway-*.log -Tail 25 -Encoding UTF8
""
"--- ACL thu muc du lieu ---"
icacls 'C:\ProgramData\HinoGateway'
```

**Kết quả đúng — bốn thứ cùng lúc:**

1. `live = 200` và `Healthy`
2. Log có `Gateway data root: C:\ProgramData\HinoGateway`
3. Log **không** có `Could not restrict permissions on the data root`
4. ACL chỉ còn đúng ba mục, và `HinoInstaller` đứng đầu:

```
C:\ProgramData\HinoGateway  TÊN-MÁY\HinoInstaller:(OI)(CI)(F)
                            NT AUTHORITY\SYSTEM:(OI)(CI)(F)
                            BUILTIN\Administrators:(OI)(CI)(F)
```

Điểm 4 là bằng chứng mạnh nhất và cũng gián tiếp chứng minh điểm 3: danh sách đó **do chính gateway
ghi ra** dưới danh nghĩa tài khoản đang chạy nó. Thấy `HinoInstaller` ở đó nghĩa là service đang chạy
bằng tài khoản đúng **và** có đủ quyền Full control.

*(Nếu log hiện ký tự lạ kiểu `â€"`, đó chỉ là hiển thị: file log là UTF-8 còn `Get-Content` của
PowerShell 5.1 mặc định đọc theo ANSI. Thêm `-Encoding UTF8` là hết.)*

---

## Bước 7 — Nghiệm thu

**Để làm gì:** chứng minh trạm chịu được hai thứ chắc chắn sẽ xảy ra ngoài hiện trường — mất điện, và
một sự cố khiến tiến trình chết.

### 7a. Phép thử khởi động lại máy

Đây là phép thử hay bị bỏ qua nhất và cũng hay lộ lỗi nhất.

```powershell
Restart-Computer
```

Sau khi máy lên, **không đăng nhập vội** — chờ khoảng một phút rồi mới đăng nhập và chạy:

```powershell
Get-Service HinoGateway | Format-List Name, Status, StartType
curl.exe -s -o NUL -w "live  = %{http_code}`n" http://localhost:2000/health/live
curl.exe -s http://localhost:2000/health/ready
```

**Kết quả đúng:** `Running` / `Automatic` / `200` / `Healthy` mà **không cần ai làm gì cả**.

**Nếu service không tự lên:** kiểm tra `StartType` có phải `Automatic` không; nếu thư mục dữ liệu nằm
trên ổ mạng hoặc ổ ngoài thì có thể service khởi động trước khi ổ sẵn sàng — chuyển sang
`Automatic (Delayed Start)` bằng `sc.exe config HinoGateway start= delayed-auto`.

### 7b. Phép thử tự phục hồi

```powershell
Stop-Process -Name HinoGateway.Server -Force
Start-Sleep -Seconds 12
Get-Service HinoGateway | Format-List Name, Status
```

**Kết quả đúng:** `Running` trở lại — Windows đã tự dựng lại nhờ `sc failure` ở bước 5.

**Nếu vẫn `Stopped`:** `sc.exe failure HinoGateway` và `sc.exe qfailure HinoGateway` để xem cấu hình
phục hồi đã được ghi chưa; nhiều khả năng thiếu `sc failureflag HinoGateway 1`.

### 7c. Bảng nghiệm thu

| # | Kiểm tra | Lệnh / thao tác | Đạt khi |
| --- | --- | --- | --- |
| 1 | Service | `Get-Service HinoGateway` | `Running`, `Automatic` |
| 2 | Tài khoản | `Get-CimInstance Win32_Service -Filter "Name='HinoGateway'"` | `StartName` = `TÊN-MÁY\HinoInstaller` |
| 3 | Liveness | `curl http://localhost:2000/health/live` | HTTP 200 |
| 4 | Readiness | `curl http://localhost:2000/health/ready` | `Healthy` |
| 5 | Thành phần nền | `curl http://localhost:2000/api/health/components` | Không có mục stopped / stale / crash-loop |
| 6 | Giao diện | Mở `http://localhost:2000` | Trang lên, các trang `/connections` `/tags` `/alarms` `/iec104` `/mqtt` mở được |
| 7 | Thư mục dữ liệu | `dir C:\ProgramData\HinoGateway` | Có `logs`, `audit`, `mqtt` |
| 8 | Quyền | `icacls C:\ProgramData\HinoGateway` | Đúng ba mục như bước 6 |
| 9 | Log | `Get-Content ...\logs\*.log -Tail 40 -Encoding UTF8` | Không có ERROR/FATAL lặp lại, không có cảnh báo quyền |
| 10 | Khởi động lại máy | Bước 7a | Tự lên, dữ liệu còn nguyên |
| 11 | Tự phục hồi | Bước 7b | Tự chạy lại trong ~5 giây |
| 12 | Ổn định | Để chạy ≥ 30 phút rồi xem lại 3, 4, 9 | Không suy giảm, RAM không tăng đều |

---

## Bước 8 — Cấu hình vận hành

Đến đây hạ tầng đã xong. Phần còn lại là cấu hình nhà máy.

**Cấu hình thiết bị:** mở `http://localhost:2000` → trang **Connections** thêm kết nối (Modbus TCP /
Siemens S7 / Simulator), trang **Tags** thêm tag, trang **Alarm Rules** thêm cảnh báo.

**Hoặc nạp từ file `.hino` có sẵn:**

```powershell
Invoke-RestMethod 'http://localhost:2000/api/config/import?replace=true' -Method Post `
  -ContentType 'application/json' -InFile 'C:\Temp\du-an.hino'
Invoke-RestMethod 'http://localhost:2000/api/config/reload' -Method Post
```

File `.hino` **cố tình không chứa bí mật** (mật khẩu MQTT…) và không chứa giá trị retentive — sau khi
import phải vào trang MQTT nhập lại thông tin đăng nhập. `reload` kiểm tra trước khi áp dụng: dự án
không hợp lệ thì runtime **giữ nguyên** và API trả về danh sách lỗi cụ thể.

**Mở truy cập từ máy khác** (mặc định gateway chỉ nghe trên chính IPC):

```powershell
@'
{
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:2000" } } }
}
'@ | Set-Content 'C:\Program Files\HinoGateway\appsettings.Production.json' -Encoding utf8

New-NetFirewallRule -DisplayName "HinoGateway HTTP" -Direction Inbound -Protocol TCP -LocalPort 2000 -Action Allow
Restart-Service HinoGateway
```

> Mở ra mạng thì **phải** cân nhắc bật bảo mật (API key + HTTPS) — xem mục 7.3 của
> `HUONG_DAN_TRIEN_KHAI_IPC.md`. Khi bảo mật tắt, request từ chính máy đó có quyền Admin còn request
> từ nơi khác chỉ đọc; như vậy vẫn là toàn bộ dữ liệu nhà máy hiển thị cho bất kỳ ai vào được cổng 2000.

**Sao lưu** — sao lưu **toàn bộ thư mục dữ liệu**, không chỉ file `.hino`:

```powershell
$stamp = Get-Date -Format yyyyMMdd-HHmm
Compress-Archive -Path C:\ProgramData\HinoGateway\* -DestinationPath "D:\Backup\hino-data-$stamp.zip"
```

---

## Những chỗ dễ vấp — tổng hợp từ lần cài thực tế

| Hiện tượng | Thực chất là gì | Xử lý |
| --- | --- | --- |
| `Read-Host` in ra một chuỗi rồi "đứng im" | Nó **đang chờ bạn gõ mật khẩu**. Chuỗi trong lệnh là nhãn nhắc, không phải mật khẩu | Gõ mật khẩu rồi Enter. Với `-AsSecureString` thì không hiện ký tự |
| Hộp thoại `Get-Credential` bung tooltip "Examples" | Nó **từ chối định dạng `.\tài-khoản`**, chưa hề kiểm tra mật khẩu | Dựng credential bằng lệnh với dạng `$env:COMPUTERNAME\tài-khoản` (bước 5) |
| `New-Service : The specified service already exists` | Service có từ trước; `-Credential` **không được áp dụng**, service vẫn chạy `LocalSystem` — trong khi mọi thứ khác vẫn báo xanh | Kiểm tra `StartName` (bước 6) và sửa bằng `sc.exe config` |
| Đổi service sang tài khoản riêng xong thì nó không ghi được dữ liệu | Lần chạy trước dưới `LocalSystem` **đã xoá ACE** của tài khoản riêng khi gateway ghi lại ACL | Cấp lại `icacls ... /grant` **trước** khi đổi tài khoản và chạy lại |
| Service báo lỗi **1069 — logon failure** | Thường là thiếu quyền *Log on as a service*, chứ không phải sai mật khẩu | Chạy lại bước 4b, kiểm tra SID có trong `SeServiceLogonRight` |
| Log hiện `â€"` và ký tự lạ | File log là UTF-8, `Get-Content` của PowerShell 5.1 đọc theo ANSI | Thêm `-Encoding UTF8` |
| Máy khác không mở được giao diện | Kestrel mặc định **chỉ nghe `localhost`** | Đổi bind sang `0.0.0.0` **và** mở tường lửa (bước 8) |
| Gateway chạy nhưng "không có thiết bị nào" | Chưa có `hino.json` — Production cố tình khởi động rỗng, không tạo demo | Cấu hình trên giao diện hoặc import `.hino` (bước 8) |
| Log ghi cảnh báo `Could not restrict permissions on the data root` | Tài khoản service chỉ có Modify, thiếu quyền *Change permissions* | `icacls 'C:\ProgramData\HinoGateway' /grant "HinoInstaller:(OI)(CI)F"` rồi restart service |

# HinoGateway — Cài đặt nhanh trên IPC

Bản dành cho người đi lắp đặt. Làm đúng 3 việc, không cần hiểu bên trong.

Bản chi tiết (giải thích từng bước và cách kiểm tra): [`IPC_CAI_DAT_CHI_TIET.md`](IPC_CAI_DAT_CHI_TIET.md).

---

## Việc 1 — Chép thư mục chương trình

Chép thư mục publish (thư mục có file `HinoGateway.Server.exe`) vào IPC, đặt tại đúng đường dẫn:

```
C:\Program Files\HinoGateway
```

Sau khi chép, file `C:\Program Files\HinoGateway\HinoGateway.Server.exe` phải tồn tại — nếu nó nằm sâu thêm một cấp thì dời nội dung lên.

## Việc 2 — Chạy script cài đặt

Mở **PowerShell bằng quyền Administrator** (chuột phải → *Run as administrator*), dán **toàn bộ** khối dưới đây và Enter.

Script sẽ hỏi mật khẩu của tài khoản `HinoInstaller` một lần. Dùng đúng mật khẩu quy định của đơn vị.

```powershell
$ErrorActionPreference = 'Stop'
if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Phai mo PowerShell bang quyen Administrator.'
}

$ProgramDir = 'C:\Program Files\HinoGateway'
$DataDir    = 'C:\ProgramData\HinoGateway'
$Account    = 'HinoInstaller'
$Service    = 'HinoGateway'

if (-not (Test-Path "$ProgramDir\HinoGateway.Server.exe")) {
  throw "Chua thay $ProgramDir\HinoGateway.Server.exe - lam Viec 1 truoc."
}

$pw = Read-Host -AsSecureString "Mat khau cho tai khoan $Account"

# --- Thu muc du lieu ---
New-Item -ItemType Directory -Force $DataDir | Out-Null

# --- Don file cua may lap trinh ---
Remove-Item "$ProgramDir\appsettings.Development.json" -Force -ErrorAction SilentlyContinue
Remove-Item "$ProgramDir\hino.json","$ProgramDir\hino.json.bak","$ProgramDir\hino.secrets.json",
            "$ProgramDir\hino.retain.json" -Force -ErrorAction SilentlyContinue

# --- Tai khoan chay service ---
if (Get-LocalUser -Name $Account -ErrorAction SilentlyContinue) {
  Set-LocalUser -Name $Account -Password $pw
} else {
  New-LocalUser -Name $Account -Password $pw -PasswordNeverExpires -AccountNeverExpires `
    -UserMayNotChangePassword -Description 'HinoGateway service account' | Out-Null
}

# --- Quyen "Log on as a service" ---
$sid = (New-Object System.Security.Principal.NTAccount($Account)).Translate([System.Security.Principal.SecurityIdentifier]).Value
$exp = "$env:TEMP\ur-export.inf"; $inf = "$env:TEMP\ur-apply.inf"; $db = "$env:TEMP\ur-apply.sdb"
secedit /export /areas USER_RIGHTS /cfg $exp | Out-Null
$cur = Get-Content $exp | Where-Object { $_ -match '^SeServiceLogonRight' }
if ($cur) { $list = ($cur -split '=', 2)[1].Trim() } else { $list = '' }
if ($list -notlike "*$sid*") {
  if ($list) { $list = "$list,*$sid" } else { $list = "*$sid" }
  @('[Unicode]','Unicode=yes','[Version]','signature="$CHICAGO$"','Revision=1',
    '[Privilege Rights]', "SeServiceLogonRight = $list") | Set-Content -Path $inf -Encoding Unicode
  secedit /configure /db $db /cfg $inf /areas USER_RIGHTS | Out-Null
}

# --- Quyen thu muc (phai xong TRUOC khi service chay lan dau) ---
if (Get-Service $Service -ErrorAction SilentlyContinue) { Stop-Service $Service -Force -ErrorAction SilentlyContinue }
icacls $DataDir    /grant "${Account}:(OI)(CI)F"  | Out-Null
icacls $ProgramDir /grant "${Account}:(OI)(CI)RX" | Out-Null

# --- Tao / cap nhat service ---
$bstr    = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($pw)
$pwPlain = [Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
if (Get-Service $Service -ErrorAction SilentlyContinue) {
  sc.exe config $Service start= auto obj= "$env:COMPUTERNAME\$Account" password= "$pwPlain" | Out-Null
} else {
  $cred = New-Object System.Management.Automation.PSCredential("$env:COMPUTERNAME\$Account", $pw)
  New-Service -Name $Service -BinaryPathName "`"$ProgramDir\HinoGateway.Server.exe`"" `
    -DisplayName 'HinoGateway' -StartupType Automatic -Credential $cred | Out-Null
}
[Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr); $pwPlain = $null

sc.exe description $Service "Industrial edge gateway - Modbus/S7 to IEC-104/MQTT" | Out-Null
sc.exe failure     $Service reset= 86400 actions= restart/5000/restart/10000/restart/30000 | Out-Null
sc.exe failureflag $Service 1 | Out-Null

# --- Chay ---
Start-Service $Service
Start-Sleep -Seconds 6
Get-Service $Service | Format-List Name, Status, StartType
Write-Host ""
Write-Host "Xong. Mo trinh duyet tren IPC: http://localhost:2000"
```

**Kết quả đúng:** dòng cuối in ra `Status : Running` và `StartType : Automatic`.

Nếu script dừng giữa chừng và in chữ đỏ, chụp lại màn hình gửi người phụ trách — đừng chạy lại nhiều lần.

## Việc 3 — Mở giao diện

Mở trình duyệt **trên chính IPC**:

```
http://localhost:2000
```

Giao diện lên là xong phần cài đặt. Lúc này gateway chưa có thiết bị nào — phần cấu hình thiết bị do người kỹ thuật làm sau.

---

## Bàn giao lại những thông tin này

| Mục | Giá trị |
| --- | --- |
| Thư mục chương trình | `C:\Program Files\HinoGateway` |
| Thư mục dữ liệu | `C:\ProgramData\HinoGateway` |
| Tên service | `HinoGateway` |
| Tài khoản chạy service | `HinoInstaller` (cục bộ) |
| Địa chỉ giao diện | `http://localhost:2000` |

---

## Chỉ dùng khi cần truy cập từ máy khác

Mặc định gateway **chỉ nghe trên chính IPC**. Chỉ chạy phần này khi được yêu cầu:

```powershell
@'
{
  "Kestrel": { "Endpoints": { "Http": { "Url": "http://0.0.0.0:2000" } } }
}
'@ | Set-Content 'C:\Program Files\HinoGateway\appsettings.Production.json' -Encoding utf8

New-NetFirewallRule -DisplayName "HinoGateway HTTP" -Direction Inbound -Protocol TCP -LocalPort 2000 -Action Allow | Out-Null
Restart-Service HinoGateway
```

> Mở ra mạng mà chưa bật bảo mật thì bất kỳ ai vào được cổng 2000 cũng xem được dữ liệu. Việc bật bảo mật (API key + HTTPS) do người kỹ thuật quyết định — xem mục 7.3 của `HUONG_DAN_TRIEN_KHAI_IPC.md`.

---

## Lệnh vận hành hằng ngày

```powershell
Get-Service HinoGateway                     # xem trang thai
Restart-Service HinoGateway                 # khoi dong lai
Get-Content C:\ProgramData\HinoGateway\logs\hinogateway-*.log -Tail 30 -Encoding UTF8   # xem log
```

# Đối chiếu bản REVIEW_2026-08-17 với mã nguồn Hino Gateway

> ## ⚠ TRẠNG THÁI FILE NÀY
>
> - **Ngày:** 2026-08-18
> - **Loại:** review-của-review. Đối chiếu từng phát hiện trong `REVIEW_2026-08-17.md` với mã nguồn
>   thực tế trong `src/`, cộng thêm một lượt rà độc lập tìm những gì bản đó bỏ sót.
> - `REVIEW_2026-08-17.md` **vẫn còn giá trị**; file này bổ sung và hiệu chỉnh nó, không thay thế.
> - Các mã `HG-Rxx` là của bản gốc. Các mã `N0x` là phát hiện mới của file này.
>
> ### ✅ ĐỢT A + B + C ĐÃ SỬA XONG — 2026-08-18, 491/491 test
>
> **Tất cả HG-R01…R16 và N01…N07 đã được xử lý bằng code, trừ ba hạng mục không thể làm bằng code**
> (xem §8). Thân bài bên dưới mô tả mã nguồn **trước khi sửa** — đọc nó như hồ sơ chẩn đoán.
>
> - Build Release: 0 warning, 0 error.
> - **413 unit test + 78 API test = 491** (từ 337 ban đầu).
> - `LoadTests -- quick` PASS; `LoadTests -- chaos` PASS (100 thiết bị × 100 tag, 120 s, 0 drop).
> - Dự án giờ có **`tests/HinoGateway.Api.Tests`** — trước đây không có một test nào ở tầng HTTP.
>
> ### Đợt A — chi tiết kiểm chứng
>
> **N01, N02, HG-R01, HG-R02, HG-R03, HG-R04, HG-R05 đã đóng.** Phần thân dưới đây mô tả mã nguồn
> **trước khi sửa** — đọc nó như hồ sơ chẩn đoán, không phải hiện trạng. §6 Đợt B và Đợt C vẫn còn
> nguyên và chưa làm.
>
> Kiểm chứng đã chạy:
> - `dotnet build HinoGateway.sln -c Release` — 0 warning, 0 error.
> - 385/385 unit test (từ 337, +48 test mới cho chính các lỗi này).
> - `LoadTests -- quick` PASS; `LoadTests -- chaos` PASS (100 thiết bị × 100 tag, 120 s).
> - **Reload nhanh hơn hẳn nhờ `AbortAsync`: p95 294 ms → 95 ms, max 294 ms → 95 ms.** Peak RSS
>   233 → 196 MB, managed heap đỉnh 137 → 92 MB.
> - N01 kiểm chứng bằng gateway chạy thật: request loopback thật vẫn được Admin (404 trên tag không
>   tồn tại = qua được authorization); cùng request kèm `X-Forwarded-For` từ proxy chưa khai → **403**.
>   Header `Forwarded` (RFC 7239) cũng vậy.
> - `GET /api/history` không có `tags` → 400; có `tags` → 200 streaming.

---

## 1. Kết luận điều hành

**Bản `REVIEW_2026-08-17.md` là một bản review đúng và có chất lượng cao.** 16/16 phát hiện đều xác
minh được trong mã nguồn; không có phát hiện nào sai sự thật, không có false positive. Đây là mức
chính xác hiếm gặp với một bản static review ở quy mô này.

Ba điều chỉnh cần ghi nhận:

1. **Ba chỗ đánh giá NHẸ HƠN thực tế** — HG-R02 (nghiêm trọng hơn nhiều, và nguyên nhân gốc nằm ở chỗ
   khác), HG-R12, HG-R13.
2. **Hai chỗ khuyến nghị hoặc mô tả tác động chưa chính xác** — HG-R01 (khuyến nghị sẽ tái tạo lại một
   bug đã sửa), HG-R14 (mô tả nhầm mô hình hosting của Blazor).
3. **Bảy vấn đề chưa được nêu (N01–N08)**, trong đó **N01 là một lỗ hổng bảo mật P0** nghiêm trọng hơn
   mọi phát hiện trong bản gốc — và nó phát sinh khi làm **đúng** theo tài liệu triển khai của chính
   dự án.

**Kết luận phê duyệt của bản gốc (mục 8) là hợp lý và tôi đồng ý**, với hai điều chỉnh ở §5.

---

## 2. Phán quyết từng phát hiện HG-R01 → HG-R16

| ID | Phán quyết | Ghi chú ngắn |
|---|---|---|
| HG-R01 | ✅ Đúng sự kiện — ⚠️ **khuyến nghị sai** | Code cố ý làm vậy; README mới là chỗ sai. Xem §2.1 |
| HG-R02 | ✅ Đúng — ⚠️ **nhẹ hơn thực tế nhiều** | Nguyên nhân gốc ở tầng IEC, không phải hash partition. Xem §2.2 |
| HG-R03 | ✅ Đúng — ⚠️ **thiếu điểm khuếch đại** | Không cần xác thực gì để kích hoạt. Xem §2.3 |
| HG-R04 | ✅ **Đúng hoàn toàn** | Còn nặng hơn: UI không cho đặt WriteMin/Max cho tag Bool nên không có lưới an toàn nào |
| HG-R05 | ✅ Đúng — ⚠️ thiếu một chi tiết kỹ thuật | `PollDeadlineMs` không cắt được lần đọc đang treo. Xem §2.5 |
| HG-R06 | ✅ Đúng | Bổ sung: ring buffer 2000 (`Program.cs:82-83`) ghi đè im lặng, không đếm số bản ghi mất |
| HG-R07 | ✅ **Đúng hoàn toàn** | `AppendAsync` trả `Task.CompletedTask` sau `catch` (`JsonlSampleHistory.cs:82-90`); sampler vẫn `Heartbeat` (`HistorySamplerService.cs:100`) |
| HG-R08 | ✅ Đúng — ⚠️ **phạm vi quá rộng** | Đường history **đã** chặn NaN (`JsonlSampleHistory.cs:138-139`). Chỉ alarm / MQTT / IEC là hở |
| HG-R09 | ✅ Đúng — ⚠️ thiếu một điểm nặng hơn | `RetainedValueStore.cs:104` **không truyền `backupPath`** → file retained không có `.bak` nào cả |
| HG-R10 | ✅ Đúng | `EndTransaction` gán `_project` ở dòng 179 rồi mới `Save()` ở dòng 200 (`JsonProjectStore.cs`) |
| HG-R11 | ✅ Đúng | `ProjectValidator` không có bất kỳ giới hạn số lượng nào; 1 OS thread / device (`WorkerThread.cs`) |
| HG-R12 | ✅ Đúng — ⚠️ **nhẹ hơn thực tế** | `CleanStart(false)` biến đây thành lỗi làm **sai dữ liệu**. Xem §2.12 |
| HG-R13 | ✅ Đúng — ⚠️ **nhẹ hơn thực tế** | Bit rot giữa segment xoá sạch mọi record hợp lệ phía sau. Xem §2.13 |
| HG-R14 | ✅ Đúng sự kiện — ❌ **mô tả tác động sai** | Blazor **WebAssembly**, không phải Blazor Server. Xem §2.14 |
| HG-R15 | ✅ Đúng — ⚠️ **over-scoped** | Ký số / process isolation là tiêu chuẩn của marketplace mở, không phù hợp sản phẩm này. Xem §2.15 |
| HG-R16 | ✅ Đúng | Code đã tự ghi chú deviation này rất trung thực (`Iec104Server.cs:655-663`). Xử lý đúng mực |

### 2.1 HG-R01 — Đúng sự kiện, nhưng khuyến nghị sẽ tái tạo lại một bug đã sửa

Migration v6→v7 **có** đặt `AllowWrite = true` (`JsonProjectStore.cs:140`), và `README.md` dòng 91
**có** khẳng định ngược lại ("Migration deliberately does not turn `AllowWrite` on"). Mâu thuẫn là
thật, bản review phát hiện đúng.

Nhưng comment ngay phía trên đoạn code đó (`JsonProjectStore.cs:122-128`) giải thích rõ lý do:

> *"In a version 6 file, mapping a point as commandable WAS the authorisation — so carrying only
> RequireSelect across left every such tag read-only, and the project validator then rejected the
> whole file. That turned a routine restore from a .hino backup into a dead end."*

Nghĩa là trong định dạng v6, **việc map một điểm thành commandable CHÍNH LÀ sự cho phép ghi**.
Migration không mang `AllowWrite` sang thì mọi tag đó thành read-only, rồi `ProjectValidator.cs:440-441`
từ chối cả file. Đây là một bug đã từng xảy ra thật và đã được sửa có chủ đích.

**Vậy nên: code đúng, README sai.** Migration không "tự bật quyền ghi" — nó **giữ nguyên** quyền đã có
trong file gốc, đúng nghĩa của một migration. Khuyến nghị *"Không tự bật `AllowWrite`; yêu cầu xác nhận
thủ công từng điểm"* sẽ khôi phục lại đúng cái ngõ cụt kia.

**Hướng xử lý đã chốt:** giữ nguyên hành vi của code; sửa `README.md` dòng 91 cho khớp; bổ sung một
cảnh báo migration liệt kê **tên từng tag vừa được bật**, hiển thị trên UI trước khi Apply. Người vận
hành cần *biết*, không cần bị chặn.

### 2.2 HG-R02 — Đúng, nhưng nguyên nhân gốc nằm ở chỗ khác và nghiêm trọng hơn nhiều

Bản review quy trách nhiệm cho việc hash device vào 8 partition cố định:

```csharp
// ControlCommandService.cs:75-76
var key = _engine.WritePartitionKey(command.TagId);
var partition = (int)((uint)StringComparer.Ordinal.GetHashCode(key) % (uint)_partitions.Length);
```

Điều đó đúng — với `WorkerCount = 8` mặc định, chỉ cần 4–5 device là gần như chắc chắn có va chạm.
Nhưng đây **không phải nút thắt chính**.

Nút thắt thật nằm ở `Iec104Server.CommandPumpAsync` (`Iec104Server.cs:943-957`):

```csharp
while (await WaitForUpdatesAsync(queue.Reader, "command dispatcher", ct))
{
    while (queue.Reader.TryRead(out var command))
    {
        Interlocked.Decrement(ref _pendingCommands);
        if (!await command.Confirmed.Task.WaitAsync(ct)) continue;
        await ExecuteCommandAsync(command, ct);   // <-- tuần tự tuyệt đối
        Pulse("command dispatcher");
    }
}
```

Channel là `SingleReader = true` (`Iec104Server.cs:153`) và vòng lặp `await` từng lệnh một. **Toàn bộ
trạm IEC-104 xử lý một lệnh tại một thời điểm**, không có phân vùng nào ở tầng này. Việc
`ControlCommandService` bên dưới có 8 partition trở nên vô nghĩa khi tầng trên chỉ đẩy xuống một lệnh
mỗi lần.

**Định lượng với giá trị mặc định** (`Iec104Contracts.cs:130,136`): `CommandTimeoutMs = 10000`,
`CommandQueueCapacity = 256`.

> Nếu một PLC chết, một lệnh gửi tới PLC khoẻ nằm sau 255 lệnh trong queue phải chờ
> **256 × 10 s = 2.560 giây ≈ 42 phút**.

Với `CommandQueueCapacity` đặt tới max 10.000 và `CommandTimeoutMs` tới max 600.000 ms
(`Iec104Server.cs:1043`), con số này trở nên vô nghĩa hoàn toàn.

Cam kết trong `README.md` dòng 95 — *"Commands are serialized per device connection, so their admission
order is preserved without blocking unrelated PLCs"* — **không đúng ở cả hai tầng**.

**Đánh giá lại mức độ:** đây không phải "High". Với một trạm nhiều PLC có điều khiển, đây là
**Blocker**, ngang hàng HG-R01. Khuyến nghị của bản review (queue riêng theo physical connection) là
đúng hướng, nhưng phải áp cho **cả hai tầng**, và **tầng IEC phải sửa trước** vì nó là tầng nghẽn thật.

### 2.3 HG-R03 — Đúng, nhưng thiếu điểm khuếch đại quan trọng nhất

Xác nhận nguyên văn: `EnergyController.cs:73-84` materialize toàn bộ vào `List<HistoryFrameView>`,
cap 100.000 frame (dòng 72), và không truyền `tags` thì lấy mọi tag của mọi frame.

**Điều bản review bỏ sót:** `Security:Enabled` **mặc định là `false`** (`appsettings.json`), và khi
tắt, mọi request từ xa được cấp thẳng role `Viewer`:

```csharp
// GatewaySecurity.cs:108-116
if (!_security.Enabled)
    return Task.FromResult(_security.AllowLocalConsole && IsLocalRequest()
        ? Success("local-console", GatewayPolicies.Admin)
        : Success("anonymous", GatewayPolicies.Viewer));
```

`EnergyController` yêu cầu đúng `[Authorize(Policy = GatewayPolicies.Viewer)]`. Nghĩa là **bất kỳ ai
chạm được tới cổng 2000 đều gọi được endpoint này mà không cần bất kỳ thông tin xác thực nào**. Đây
không phải "một yêu cầu sai hoặc cố ý" của người dùng hợp lệ như bản review viết — đây là **DoS từ xa
không cần xác thực**, và nên được xếp ngang HG-R01/HG-R02.

**Một điều chỉnh theo hướng ngược lại, để công bằng:** retention là 7 ngày × 1 phút ≈ 10.080 frame
(`appsettings.json`: `RetentionDays: 7`, `IntervalSeconds: 60`), nên cap 100.000 không bao giờ đạt tới
trên thực tế. Kịch bản "10.000 tag → hàng tỷ giá trị" của bản review là quá đà: chính comment trong
`JsonlSampleHistory.cs:20-22` xác định site lớn là 500 tag. Nhưng 10.080 × 500 ≈ **5 triệu giá trị
trung gian cho MỘT request**, và không có giới hạn số request đồng thời, không timeout, không streaming.
Rủi ro giữ nguyên; chỉ là độ lớn khác con số bản review đưa ra.

### 2.5 HG-R05 — Đúng, thiếu một chi tiết làm khuyến nghị chưa đủ chặt

Xác nhận đầy đủ:

- `StopDeadline = TimeSpan.FromSeconds(10)` — `DeviceWorker.cs:73`, dùng ở dòng 354.
- `TimeoutMs` cho phép tới 600.000 ms, `Retries` tới 10 — `ModbusTcpDriverFactory.cs:17-18`.
- Các lời gọi NModbus **không nhận cancellation token** — `ModbusTcpDriver.cs:148-159`.

**Chi tiết bị bỏ sót:** `PollDeadlineMs` chỉ được kiểm tra **giữa các block**, không cắt được một lần
đọc đang treo:

```csharp
// ModbusTcpDriver.cs:136 — nằm TRƯỚC lời gọi đọc, vô tác dụng khi lần đọc đã bắt đầu
if (budget.ElapsedMilliseconds > _pollDeadlineMs) { Fail(block, ...); continue; }
```

Nên khuyến nghị *"Giới hạn timeout × retries nhỏ hơn shutdown deadline"* là đúng hướng nhưng chưa đủ.
Ràng buộc thật phải là:

> `ConnectTimeout + TimeoutMs × (Retries + 1) < StopDeadline`

và phải được **cưỡng chế trong `Validate()` của factory**, không chỉ ghi vào tài liệu — vì đây đúng là
chỗ mà một người vận hành thiện chí sẽ nâng timeout lên để "cho chắc" trên một link chậm.

### 2.12 HG-R12 — Đúng, nhưng hậu quả nặng hơn "đá kết nối của nhau"

Xác nhận: `MqttContracts.cs:30` — `ClientId = "hinogateway"`; fallback machine-name chỉ chạy khi rỗng
(`MqttPublisher.cs:291`).

Điều bản review không nêu: dòng ngay bên dưới là `.WithCleanStart(false)` (`MqttPublisher.cs:292`).
ClientId trùng **cộng với** persistent session nghĩa là hai gateway **chia sẻ chung một session bền
trên broker**. Hậu quả không chỉ là ngắt kết nối lẫn nhau:

- Message QoS 1 đang chờ (inflight/queued) của gateway A **có thể được broker giao cho gateway B**.
- Subscription của máy này áp lên máy kia.
- Reconnect storm khiến disk spool của **cả hai** máy quay vòng liên tục, ăn mòn flash của IPC.

Đây là lỗi làm **sai dữ liệu**, không chỉ làm mất kết nối. Với một gateway gửi số liệu năng lượng cho
đơn vị mua điện, đó là chuyện khác hẳn về bản chất.

### 2.13 HG-R13 — Đúng, nhưng mô tả chưa hết mức mất mát

Bản review viết "truncate phần còn lại của segment" — đúng chữ, nhưng chưa nói ra hệ quả. Trong
`RecoverSegment` (`MqttDiskSpool.cs:274-282`):

```csharp
if (Checksum(payload) != expectedChecksum)
{
    TruncateTornTail(stream, recordOffset);
    break;                       // <-- bỏ luôn mọi record hợp lệ phía sau
}
```

Với **torn tail** (record cuối bị cắt do mất điện) đây là hành vi đúng. Nhưng với **bit rot ở giữa
segment**, nó cắt bỏ toàn bộ các record **hợp lệ** nằm sau điểm hỏng. Một bit sai gần đầu một segment
8 MB có thể xoá sạch cả segment.

Và `TruncateTornTail` (`MqttDiskSpool.cs:313-318`) chỉ có 3 dòng: `SetLength` / `Position` / `Flush`.
**Không log, không đếm, không cách ly, không báo health.** Dữ liệu biến mất không để lại dấu vết nào.

**Khuyến nghị bổ sung:** phân biệt torn-tail (cắt, hợp lệ) với corruption giữa file (bỏ qua record
hỏng, **giữ các record sau**, đưa phần hỏng vào `quarantine/`); trong cả hai trường hợp phải log, đếm
số byte/record mất, và đẩy lên `ComponentHealthRegistry`.

### 2.14 HG-R14 — Đúng sự kiện, sai mô tả tác động

Xác nhận thiếu `catch`: `Connections.razor` 6 `try` / 2 `catch`, `Tags.razor` 7/4, `Alarms.razor` 5/3,
`AlarmRules.razor` 3/2.

Nhưng `src/HinoGateway.Web/HinoGateway.Web.csproj` dùng
`<Project Sdk="Microsoft.NET.Sdk.BlazorWebAssembly">` — đây là **Blazor WebAssembly, không phải Blazor
Server**. Nên tác động không phải "unhandled exception làm sập circuit": WASM không có SignalR circuit
để mà sập.

Tác động **thật**, và vẫn nghiêm trọng theo cách riêng của nó với một màn hình SCADA:

> Màn hình vận hành **đứng im hiển thị dữ liệu cũ**. Không banner offline, không dấu stale, không thời
> điểm cập nhật cuối. Người vận hành nhìn vào một con số đã chết và tin đó là số hiện tại.

Với HMI công nghiệp, "hiển thị số cũ mà không nói" là chế độ hỏng **tệ hơn** "báo lỗi và không hiển thị
gì". Khuyến nghị của bản review (offline banner, last-updated, retry có backoff, xử lý thống nhất
`HttpRequestException` / timeout / 401 / 503) vẫn đúng nguyên vẹn — chỉ cần sửa lại mô tả cơ chế.

### 2.15 HG-R15 — Đúng, nhưng đặt tiêu chuẩn quá cao cho sản phẩm này

Xác nhận: `Program.cs:157-159` đăng ký cứng 3 factory (Simulator, ModbusTCP, SiemensS7).

Nhưng yêu cầu *"manifest, version compatibility, **ký số**, discovery hoặc **process isolation**"* là
tiêu chuẩn của một plugin marketplace mở cho bên thứ ba, không phải của một gateway triển khai lên một
tập IPC cố định do chính đội mình quản lý và build.

Dự án đã có `DriverDescriptor` + UI generic + `IDeviceDriverFactory.Validate()` / `ValidateTags()` —
tức là **đã có phần đắt giá nhất** của một plugin contract: bề mặt cấu hình được mô tả bằng dữ liệu và
kiểm tra tự động (`ProjectValidator.cs:600-660`).

Giữ nguyên mức Medium và Giai đoạn 3, nhưng **thu hẹp phạm vi** còn: assembly discovery từ thư mục
`drivers/`, kiểm tra version contract, và một driver conformance suite. Bỏ ký số và process isolation
trừ khi có yêu cầu cụ thể từ khách hàng.

---

## 3. Bảy vấn đề bản review chưa nêu

### N01 — 🔴 P0 BẢO MẬT: reverse proxy cùng máy biến mọi người dùng từ xa thành Admin

**Mức độ: Blocker. Nghiêm trọng hơn mọi phát hiện trong bản gốc.**

`src/HinoGateway.Server/Security/GatewaySecurity.cs:136-143`:

```csharp
private bool IsLocalRequest()
{
    var remote = Context.Connection.RemoteIpAddress;
    if (remote is null)
        return true;                     // in-process call
    var local = Context.Connection.LocalIpAddress;
    return System.Net.IPAddress.IsLoopback(remote) || remote.Equals(local);
}
```

Khi `Security:Enabled = false` — **giá trị mặc định trong `appsettings.json`** — một request được coi
là "local" sẽ được cấp thẳng **Admin** (`GatewaySecurity.cs:114`): toàn quyền ghi tag, gửi lệnh tới
PLC, sửa cấu hình, import project.

**Không có `UseForwardedHeaders` ở bất kỳ đâu trong `src/`** (đã grep toàn bộ). Nghĩa là:

> Khi đặt IIS/nginx **trên chính IPC** để kết thúc TLS, mọi request từ LAN tới Kestrel đều mang
> `RemoteIpAddress = 127.0.0.1`. `IsLoopback` trả `true`. **Mọi người dùng từ xa trở thành Admin,
> không cần bất kỳ thông tin xác thực nào.**

Và đây **chính là topology mà tài liệu triển khai của dự án khuyến nghị** —
`HUONG_DAN_TRIEN_KHAI_IPC.md` dòng 356:

> *"`RequireHttps=false` chỉ chấp nhận được khi có reverse proxy tin cậy đã kết thúc TLS ngay trước…"*

Đây là điểm đáng lo nhất của toàn bộ đợt review: lỗ hổng **không phát sinh do cấu hình sai**, mà phát
sinh khi người triển khai làm **đúng** theo hướng dẫn của chính dự án.

**Hệ quả phụ, cũng nghiêm trọng:** audit trail sẽ ghi actor là `local-console` cho **tất cả** người
dùng từ xa. Mất hoàn toàn khả năng truy vết ai đã ra lệnh — đúng cái mà audit trail sinh ra để bảo đảm.

**Khuyến nghị:**

- Thêm `UseForwardedHeaders` với `KnownProxies` / `KnownNetworks` đọc từ config, **mặc định rỗng =
  không tin proxy nào**, đặt trước `UseAuthentication()`.
- `IsLocalRequest()` bỏ nhánh `remote is null → true` (không chứng minh được gì) và bỏ nhánh
  `remote.Equals(local)` (đúng khi bind LAN IP nhưng thừa và dễ hiểu sai). Chỉ giữ `IsLoopback`.
- Cân nhắc đảo mặc định: `AllowLocalConsole` chỉ có tác dụng khi Kestrel đang bind loopback.

### N02 — High: middleware RequireHttps không đọc `X-Forwarded-Proto`

`Program.cs:185-197` kiểm tra `context.Request.IsHttps`, luôn `false` sau một proxy đã kết thúc TLS.
Vì thế topology proxy **buộc** người triển khai đặt `RequireHttps=false` — tức tắt hẳn guard thay vì
để nó hoạt động đúng.

Sửa chung với N01: sau khi `UseForwardedHeaders` áp `X-Forwarded-Proto`, `Request.Scheme` sẽ đúng và
guard chạy được như thiết kế.

### N03 — High: không có một test nào ở tầng HTTP/API

33 file test trong `tests/HinoGateway.Engine.Tests/`, **không file nào** dùng `WebApplicationFactory`
hay đi qua HTTP pipeline. Không có `GatewaySecurityTests`, không có test cho `ProjectValidator`, không
có test cho `/api/history`.

Hệ quả trực tiếp: một endpoint mất `[Authorize]`, một policy bị hạ từ Admin xuống Viewer, hoặc chính
lỗi N01 ở trên — **không có test nào bắt được**.

Với một sản phẩm có đường điều khiển PLC, đây là khoảng trống lớn hơn bất kỳ hạng mục nào trong "Ma
trận kiểm thử hiện trường" (mục 6 của bản gốc), vì nó **tự động hoá được ngay hôm nay** chứ không cần
chờ ra hiện trường với PLC thật.

### N04 — Medium: API key lưu plaintext trong appsettings

`GatewayApiKey.Key` (`GatewaySecurity.cs:78`) bind trực tiếp từ configuration. Dự án đã có
`JsonSecretStore` với cơ chế `${secret:...}` dùng cho password MQTT, nhưng **không dùng cho API key**.

Đây là sự thiếu nhất quán trong chính mô hình bảo mật của dự án. Nên hoặc lưu hash (PBKDF2/Argon2),
hoặc dùng đúng secret store đã có sẵn.

### N05 — Medium: không đếm, không log, không giới hạn lần xác thực thất bại

Rate limiter chỉ gắn cho policy `"commands"` (`Program.cs:42-58`). Việc dò khoá API là **không giới
hạn**, và `AuthenticateResult.Fail("Invalid API key.")` (`GatewaySecurity.cs:132`) không được ghi vào
audit trail, không tăng metric nào. Không có cách nào phát hiện brute force, kể cả sau khi nó đã xảy ra.

`SecureEquals` (`GatewaySecurity.cs:154-161`) dùng `CryptographicOperations.FixedTimeEquals` — đúng và
tốt. Nhưng chống timing attack mà không chống brute force thì mới xong một nửa.

### N06 — Medium: `DurableAtomicFile` không fsync thư mục

`DurableAtomicFile.cs:71-79` flush nội dung file tới đĩa (`FileOptions.WriteThrough` +
`Flush(flushToDisk: true)`) rồi `File.Replace` / `File.Move`, nhưng **không fsync directory entry**.

Trên Windows/NTFS — mục tiêu triển khai chính — rủi ro thấp. Nhưng dự án có `UseSystemd()` và unit file
trong README, tức Linux là mục tiêu được hỗ trợ; trên ext4 thao tác rename có thể mất sau khi cắt
nguồn dù nội dung file đã bền. Đó là lỗ hổng trong chính tính chất "atomic + durable" mà lớp này đặt tên.

### N07 — Medium: chưa có ngân sách đĩa tổng cộng

Cộng các mặc định trong `appsettings.json`:

| Mục | Trần |
|---|---|
| Audit (`MaxTotalBytes`) | 2,00 GB |
| History (`MaxTotalBytes`) | 2,00 GB |
| Energy journal (`MaxTotalBytes`) | 64 MB |
| Serilog (14 file × 32 MB) | 448 MB |
| **Tổng** | **~4,5 GB** |

Trên một IPC ổ 32 GB đã có Windows, con số này cần được nêu rõ trong `HUONG_DAN_TRIEN_KHAI_IPC.md` và
trong checklist nghiệm thu. Bản gốc có nhắc "theo dõi dung lượng ổ đĩa" ở mục 6.1 nhưng không đưa ra
con số nào để đối chiếu.

*(Ghi chú: trần history 2 GB là quá rộng so với thực tế — comment trong `JsonlSampleHistory.cs:20-22`
ước tính ~12 MB/tuần cho 32 tag. Trần đó gần như không bao giờ chạm tới; `RetentionDays` mới là ràng
buộc thật.)*

### N08 — Ghi chú phạm vi: chưa có driver Modbus RTU

Cây nguồn hiện tại chỉ có `ModbusTcpDriver` (`src/HinoGateway.Drivers.Modbus/`). Đây **không phải
lỗi** — nhưng nếu kế hoạch EVN / XK3190 cần RS485 thì đây là khoảng trống **chức năng** phải đưa vào
kế hoạch. Bản review (vốn khai phạm vi là "driver Modbus TCP") không nêu điều này.

---

## 4. Bổ sung cho ma trận kiểm thử (mục 6 của bản gốc)

Ma trận của bản gốc rất đầy đủ. Bốn kịch bản còn thiếu:

1. **Apply/reload project đồng thời với một lệnh điều khiển đang chạy dở.** `ControlCommandService`
   xử lý shutdown khá kỹ (`WorkerAsync` finally block), nhưng đường reload chưa được kiểm chứng cùng
   lúc với command in-flight.
2. **Hai control centre trong cùng redundancy group gửi lệnh đồng thời tới cùng một IOA** —
   `ServerMode.CONNECTION_IS_REDUNDANCY_GROUP` (`Iec104Server.cs:172`) chưa có test nào cho tình
   huống này.
3. **Khởi động với file project bị hỏng / 0 byte / JSON cắt cụt**, và khôi phục từ `.bak` — liên quan
   trực tiếp tới HG-R09.
4. **Test phân quyền ở tầng API cho MỌI endpoint** (xem N03) — hạng mục duy nhất trong danh sách này
   không cần phần cứng, và nên làm trước tiên.

---

## 5. Đánh giá ma trận phê duyệt của bản gốc

Ma trận ở mục 8 là **hợp lý và tôi đồng ý**, với hai điều chỉnh:

| Môi trường | Bản gốc | Điều chỉnh |
|---|---|---|
| Development/demo với Simulator | Có thể sử dụng | ✅ Giữ nguyên |
| Pilot chỉ đọc, có giám sát | Có điều kiện, sau khi giới hạn history và chạy soak | ⚠️ **Thêm điều kiện đóng N01** — lỗ hổng đó cấp quyền Admin (ghi PLC) ngay cả trong một triển khai được cho là chỉ đọc |
| Pilot có lệnh điều khiển | Chưa phê duyệt trước khi đóng R01, R02, R04, R05 | ⚠️ **Thêm N01; nâng HG-R02 lên Blocker**, không phải High thường |
| Production 24/7 không giám sát | Chưa phê duyệt | ✅ Giữ nguyên |

---

## 6. Kế hoạch xử lý — đã sắp xếp lại theo thứ tự đúng

### Đợt A — Bảo mật & an toàn điều khiển — ✅ **HOÀN TẤT 2026-08-18**

| # | Hạng mục | Việc cần làm |
|---|---|---|
| A1  ✅ | **N01 + N02** | `Program.cs`: `UseForwardedHeaders` với `KnownProxies`/`KnownNetworks` từ config (mặc định rỗng), đặt trước `UseAuthentication()`. `IsLocalRequest()` bỏ nhánh `remote is null` và `remote.Equals(local)`, chỉ giữ `IsLoopback`. Middleware RequireHttps đọc `Request.Scheme` sau forwarded headers |
| A2  ✅ | **HG-R02** | `Iec104Server.CommandPumpAsync`: thay single-reader tuần tự bằng fan-out theo `DeviceEngine.WritePartitionKey`, giữ admission capacity toàn cục. Sau đó `ControlCommandService` chuyển từ hash-modulo-8 sang dictionary partition theo device, có cleanup khi reload |
| A3  ✅ | **HG-R04** | `Scaling.ToRaw` (`Scaling.cs:44-46`): tách đường control thành strict-type. Bool chỉ nhận `bool`, hoặc chính xác `0`/`1` khi tag bật cờ cho phép; tag numeric từ chối `bool`. Giữ coercion linh hoạt cho expression/internal tag qua một entry point riêng |
| A4  ✅ | **HG-R05** | Cưỡng chế `ConnectTimeout + TimeoutMs × (Retries+1) < StopDeadline` trong `ModbusTcpDriverFactory.Validate` và `SiemensS7DriverFactory.Validate`. Bổ sung `AbortAsync` vào `IDeviceDriver` để đóng socket cưỡng bức khi cancel |
| A5  ✅ | **HG-R03** | Bắt buộc tham số `tags`; giới hạn số tag/request; stream response thay vì materialize `List`; giới hạn số query đồng thời; thêm timeout + cancellation |
| A6  ✅ | **HG-R01** | Sửa `README.md` dòng 91 cho khớp code. Thêm cảnh báo migration liệt kê tên tag vừa được bật `AllowWrite`, hiển thị trên UI trước khi Apply. **Không đổi hành vi của code** |

**Ghi chú thực thi Đợt A** (khác với kế hoạch ban đầu ở hai chỗ):

- **A2**: nguyên nhân gốc ở tầng IEC đã được sửa trước (`Iec104Server` chuyển từ một pump tuần tự
  sang một lane cho mỗi thiết bị, khoá do `DeviceEngine.WritePartitionKey` cấp qua delegate
  `Iec104CommandPartitioner` mới). `ControlCommandService` bỏ hash-modulo-8, dùng
  `ConcurrentDictionary` theo device. `Commands:WorkerCount` giữ lại nhưng `[Obsolete]` và không
  còn tác dụng — x oá hẳn sẽ làm hỏng binding trên mọi gateway đã triển khai.
- **A4**: `AbortAsync` trên `IDeviceDriver` (mặc định no-op) là cơ chế chính, không phải trần
  validation — đóng socket là thứ duy nhất cắt được một lần đọc NModbus/S7netplus đang treo.
  Trần `DriverLimits.CheckCancellationBudget` là tuyến hai. Đây là thay đổi đo được: reload p95
  294 ms → 95 ms trong chaos test.

### Đợt B — Độ bền & khả năng quan sát — ✅ **HOÀN TẤT 2026-08-18**

| # | Hạng mục | Việc cần làm |
|---|---|---|
| B1  ✅ | **N03** | Dựng `tests/HinoGateway.Api.Tests` với `WebApplicationFactory`. Phủ toàn bộ ma trận endpoint × role, cộng regression test cho N01 (`X-Forwarded-For` từ known proxy → Viewer, không phải Admin) |
| B2  ✅ | **HG-R07, HG-R10** | `AppendAsync` báo lỗi lên `ComponentHealthRegistry` thay vì chỉ đếm `Dropped`. `EndTransaction` chỉ gán `_project` **sau khi** `Save()` thành công |
| B3  ✅ | **HG-R09** | `RetainedValueStore` truyền `backupPath`. Cả hai store thử `.bak` khi load hỏng, và tự schedule retry sau flush lỗi |
| B4  ✅ | **HG-R08** | Chuẩn hoá NaN/Infinity thành Bad/Uncertain kèm status text ngay tại `Scaling.Apply` (`Scaling.cs:17`) — đường history đã an toàn, cần bịt alarm/MQTT/IEC |
| B5  ✅ | **HG-R12, HG-R13** | ClientId mặc định gắn machine name. Spool: phân biệt torn-tail với corruption giữa file, giữ record hợp lệ phía sau, log + đếm + quarantine + báo health |
| B6  ✅ | **HG-R06** | Forward alarm event qua MQTT — đúng như comment `AlarmService.cs:9-11` đã hứa. Đếm số bản ghi bị ring buffer ghi đè |
| B7  ✅ | **HG-R11, HG-R14, N04, N05, N07** | Capacity limit trong `ProjectValidator`. Offline banner + stale marker + retry backoff cho UI. API key vào secret store. Log/đếm/rate-limit auth failure. Tài liệu hoá ngân sách đĩa ~4,5 GB |

### Đợt C — Trước rollout diện rộng — ✅ phần làm được bằng code đã xong

- **HG-R15** — phạm vi thu hẹp: discovery + version contract + conformance suite; bỏ ký số và process isolation.
- **HG-R16** — xác nhận deviation COT 20/37 với đúng SCADA/control centre đích trong commissioning.
- **N06** — fsync directory nếu Linux là mục tiêu được hỗ trợ chính thức.
- **N08** — quyết định về driver Modbus RTU.
- CI bắt buộc: Release build, unit/recovery, chaos, dependency scan.
- Soak 24–72 giờ trên đúng model IPC.

---

## 7. Điểm mạnh — xác nhận lại và bổ sung

Danh sách điểm mạnh ở mục 5 của bản gốc là chính xác. Bổ sung ba điểm mà bản gốc không nêu và đáng
được ghi nhận:

- **`AuditTrail` dùng `BoundedChannelFullMode.Wait`** (`AuditTrail.cs:56`), tức back-pressure chứ không
  im lặng bỏ bản ghi. Kết hợp với `ControlResult.AuditPersisted` / `AuditError` tách bạch "kết quả
  thiết bị" khỏi "kết quả ghi audit" — đây là thiết kế đúng cho một hệ có trách nhiệm pháp lý về lệnh
  điều khiển.
- **Poll loop dùng `PeriodicTimer` + `Stopwatch`** (`DeviceWorker.cs:105,108`), tức đồng hồ đơn điệu.
  NTP nhảy tiến/lùi **không** làm hỏng nhịp polling. Mục 6.4 của bản gốc vẫn nên chạy, nhưng rủi ro ở
  đây thấp hơn bản gốc ngụ ý.
- **Mật độ comment giải thích *tại sao*** trong mã nguồn cao bất thường và có giá trị thật — nhiều
  comment ghi lại đúng cái bug đã dẫn tới quyết định hiện tại (`JsonProjectStore.cs:122-128`,
  `DurableAtomicFile.cs:60-62`, `HistorySamplerService.cs:95-99`). Chính nhờ đó mà HG-R01 xác định
  được là "README sai" chứ không phải "code sai".

---

**Hình thức:** static review có dẫn chứng `file:line`, 2026-08-18.
**Không có thay đổi mã nguồn nào trong lần này.**

---

## 8. Những gì KHÔNG thể đóng bằng code

Ba hạng mục của Đợt C còn mở, và không phải vì thiếu thời gian:

| Hạng mục | Vì sao còn mở | Ai làm được |
| --- | --- | --- |
| **HG-R16** — deviation COT 20 vs 37 | Cần một control centre thật trả lời "chấp nhận" hay "không". Không giả lập được, và cũng không nên giả định | Commissioning với SCADA đích. Nếu họ bắt buộc COT 37 thì phải vá hoặc đổi thư viện lib60870.NET |
| **Soak 24–72 giờ trên IPC thật** | Cần đúng model phần cứng đích chạy liên tục nhiều ngày. Chaos test 120 giây không thay thế được — nó không thấy được fragmentation chậm hay rò handle theo ngày | Đội triển khai, trên IPC đã cài đặt |
| **N08** — có làm driver Modbus RTU không | Là quyết định sản phẩm, không phải lỗi kỹ thuật. Phụ thuộc vào việc dự án EVN/XK3190 có cần RS485 hay không | Chủ dự án |

Ngoài ra, **workflow CI ở `.github/workflows/ci.yml` đã viết nhưng chưa từng chạy** — nó cần GitHub
Actions thực thi mới biết đúng. Các bước trong đó chính là các lệnh đã chạy tay thành công ở máy
này, nhưng `-warnaserror` và bước quét package dễ cần chỉnh ở lần chạy đầu.

Còn một điểm đáng nhắc: **`GET /api/history` giờ bắt buộc tham số `tags`** và **`Commands:WorkerCount`
không còn tác dụng**. Cả hai là breaking change có chủ ý; không có caller nào trong UI/docs bị ảnh
hưởng, nhưng hệ thống ngoài gọi `/api/history` không kèm `tags` sẽ nhận 400.

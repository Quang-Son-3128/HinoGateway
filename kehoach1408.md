# Sản lượng bền vững qua mất điện + Store-and-forward MQTT không nghẽn realtime

## Context — vì sao làm

Hai vấn đề người dùng nêu có chung một gốc: **dữ liệu phải sống sót qua mất điện của IPC**.

1. **Sản lượng ngày/hôm qua/tháng hiện không có gì tính.** Trong dự án thật `EVNRIKEN_Project.hino`,
   các tag `INTMemory/Inv1EnergyYesterday` … `TotalEnergyYesterday` là **Memory tag rỗng**, nhưng đã
   được ánh xạ vào point map gửi Điện lực: **IOA 3 = TotalEnergyYesterday**, **IOA 14/16/18 =
   Inv1..3EnergyYesterday** — đúng điểm Ainv_D-1 mà QĐ 378/EVN bắt buộc. Đây là **hạng mục chặn
   nghiệm thu**, không phải tính năng phụ. Bản NanoX cũ làm bằng script C# (`vars["start"]`, reset
   khi sang ngày); năng lực đó chưa được mang sang HinoGateway.
2. **Mất kết nối MQTT** → phải đệm tại chỗ rồi truyền bù, nhưng không được làm trễ realtime. Cơ chế
   hiện tại là **một FIFO duy nhất** (`MqttDiskSpool`, trần 256 MB): batch mới nối đuôi backlog nên
   sau 1 giờ mất kết nối có ~720 batch phải đẩy xong mới tới lượt dữ liệu hiện tại — đúng cái "lag"
   người dùng lo.

Ngoài ra QĐ 378 còn đòi **lưu tại chỗ độ phân giải ≤5 phút, giữ ≥7 ngày** và **truyền bù sau sự cố** —
cả hai đang thiếu. Một bộ lưu lịch sử 1 phút/7 ngày giải quyết đồng thời cả ba thứ: chuẩn EVN, mốc
0h cho sản lượng, và nguồn dữ liệu để truyền bù.

## Ràng buộc đã chốt với người dùng

| | Quyết định |
|---|---|
| Nguồn kWh | Thiết bị **có thanh ghi cộng dồn** (`Inv1Energy` = `DB1[144]`, **UInt32**, scale 0.01 → 0,01 kWh/đơn vị) |
| Kịch bản mất điện | **Chỉ IPC tắt, inverter vẫn chạy và vẫn đếm** |
| Server MQTT | Của người dùng → tự do thiết kế payload/topic, server chống trùng theo (gateway, tag, timestamp) |
| Buffer local | **1 phút × 7 ngày** |
| Cách khai báo | **`TagKind.Accumulator`** — loại tag thứ 4 |
| Phạm vi lưu lịch sử | **Tất cả tag** |
| Thiếu mốc 0h | Lấy mẫu gần nhất + **đánh `Quality.Uncertain`**, ghi rõ lý do |
| UI | **Chưa làm đợt này** — chỉ lõi + API + tag |

## Nguyên lý: vì sao phương án này đúng

Vì thanh ghi thiết bị **cộng dồn và tự đếm khi gateway chết**, sản lượng ngày **không phải là một
phép tích luỹ** mà là một **phép trừ**:

```
Sản lượng hôm nay = counter(bây giờ) − counter(lúc 0h hôm nay)
Sản lượng hôm qua = counter(0h hôm nay) − counter(0h hôm qua)
```

Hệ quả quan trọng: **chỉ cần các mốc (baseline) là bền vững**, còn giá trị hiện tại luôn đọc lại được
từ thiết bị. Một mẫu đọc lỗi chỉ làm sai đúng khoảnh khắc đó rồi tự lành — khác hẳn cách tích luỹ
`prev() + P*dt` (sai một lần là sai vĩnh viễn). Đây là lý do **không** dùng Expression để làm việc này.

Mốc 0h chỉ mất khi gateway chết **ngay lúc giao ngày** → lấy lại từ bộ lưu lịch sử.

---

## Deliverable A — `SampleHistory`: bộ lưu lịch sử 1 phút / 7 ngày

Nền móng cho cả B và C, và là hạng mục "lưu tại chỗ" của QĐ 378.

**Định dạng:** JSONL nối đuôi, một file/ngày, theo đúng khuôn `AuditTrail`
(`src/HinoGateway.Server/Services/AuditTrail.cs`) — `FileMode.Append` + `FileOptions.WriteThrough` +
`Flush(flushToDisk:true)`, retention theo **ngày HOẶC số file HOẶC tổng byte**. Chọn JSONL thay vì
nhị phân vì kỹ sư tại trạm mở đọc/`grep` được ngay, và ở quy mô thật nó rẻ: 32 tag ≈ 1,2 KB/dòng ×
1440 dòng/ngày ≈ **12 MB/tuần**. Trần byte vẫn giữ như một van an toàn cho trạm nhiều tag
(500 tag ≈ 200 MB/tuần).

```
<DataRoot>/history/hist-20260814.jsonl
{"t":"2026-08-14T00:01:00+07:00","v":{"Inverter/Inv1Energy":[123456.78,2],"PowerMeter/Hz":[50.01,2]}}
```
`[giá trị, quality]` với quality 2=Good/1=Uncertain/0=Bad; tag không có giá trị thì vắng mặt.

**File cần tạo**
- `src/HinoGateway.Core/ISampleHistory.cs` — hợp đồng để Engine dùng mà không phải tham chiếu Storage:
  ```csharp
  public sealed record HistorySample(double Value, Quality Quality);
  public sealed record HistoryFrame(DateTimeOffset Timestamp, IReadOnlyDictionary<string, HistorySample> Values);
  public interface ISampleHistory {
      Task AppendAsync(HistoryFrame frame, CancellationToken ct = default);
      IAsyncEnumerable<HistoryFrame> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
      bool TryGetNearest(string tagId, DateTimeOffset at, TimeSpan tolerance, out HistorySample sample, out DateTimeOffset actualAt);
      bool TryGetAround(string tagId, DateTimeOffset at, out (DateTimeOffset At, HistorySample S)? before,
                                                          out (DateTimeOffset At, HistorySample S)? after);
      DateTimeOffset? OldestRetained { get; }
      (int Files, long Bytes, long Frames, long Dropped) Stats { get; }
  }
  ```
- `src/HinoGateway.Storage/JsonlSampleHistory.cs` — cài đặt; retention copy nguyên `AuditTrail.EnforceRetention`
  (kể cả bẫy đã biết: **không được xoá chính file đang mở**).
- `src/HinoGateway.Server/Services/HistorySamplerService.cs` — `BackgroundService` theo đúng khuôn tự
  khởi động lại của `AlarmService`: canh mốc phút, `TagStore.Snapshot()`, `AppendAsync`, nhịp
  `_health.Heartbeat("history-sampler")` mỗi vòng (chờ rảnh phải cắt ≤5 s, nếu không
  `ComponentHealthRegistry` coi là Stale và `/health/ready` đỏ).

**Sửa**
- `src/HinoGateway.Server/GatewayDataPaths.cs` — thêm `public string History => Path.Combine(Root, "history");`
- `src/HinoGateway.Server/Program.cs` — đăng ký singleton + `AddHostedService(sp => sp.GetRequiredService<…>())`.

**Config** (`appsettings.json`, khoá `History:`)

| Khoá | Mặc định | Ý nghĩa |
|---|---|---|
| `Enabled` | `true` | |
| `IntervalSeconds` | `60` | Chốt EVN cần ≤300 |
| `RetentionDays` | `7` | |
| `MaxFiles` | `14` | |
| `MaxTotalBytes` | `2147483648` | Van an toàn cho trạm nhiều tag |
| `Path` | (trống) | Giải qua `GatewayDataPaths.Resolve` |

---

## Deliverable B — `TagKind.Accumulator`: sản lượng bền vững

### Mô hình dữ liệu

`src/HinoGateway.Core/TagKind.cs` → thêm `Accumulator = 3` (nối tiếp, **không đánh số lại**), cập nhật
`IsInternal()` = true, `IsWritable()` = false.

`src/HinoGateway.Core/AccumulationPeriod.cs` (mới): `Day, PreviousDay, Month, PreviousMonth, Year, PreviousYear`.

`TagDefinition` (`src/HinoGateway.Core/TagDefinition.cs`) thêm — tất cả có mặc định nên file cũ vẫn nạp:
- `SourceTagId` (`string?`) — **đường dẫn runtime** của tag counter nguồn
- `Period` (`AccumulationPeriod`, mặc định `Day`)
- `RolloverAt` (`double`, 0 = không wrap) — với UInt32 × scale 0.01 thì đặt `42949672.95`
- `MaxRatePerHour` (`double?`) — chặn gai đọc; null = tắt

`TagDto` (`ConfigContracts.cs`) thêm `SourceTagKey` (**khoá lưu trữ GUID**) + `SourcePath` (server tính,
chỉ đọc), `Period`, `RolloverAt`, `MaxRatePerHour`.

> **Bắt buộc:** tham chiếu nguồn lưu bằng **khoá lưu trữ**, không phải path — id runtime là đường dẫn
> thư mục, đổi tên tag hay chuyển thư mục là mất liên kết. Đây đúng bài đã giải cho Alarm
> (`AlarmDto.TagId` = khoá lưu trữ + `TagPath` tính khi đọc, xem `AlarmMapping`); làm y hệt.

**Luật kiểm tra** (`src/HinoGateway.Server/ProjectValidator.cs` — đã có sẵn các nhánh theo kind ở
dòng 118/126/154/232, thêm một nhánh `Accumulator`): tag nguồn phải tồn tại, phải là kind **Device
hoặc Memory** (cấm nguồn là Expression — vừa chặn mọi vòng lặp chéo giữa hai engine, vừa đúng thực tế
là counter đến từ thiết bị), phải là kiểu số, `RolloverAt ≥ 0`, `MaxRatePerHour > 0` nếu đặt, và
`AllowWrite` luôn bị ép `false`.

`.hino` lên **version 8**; `JsonProjectStore.Normalise` thêm nhánh `< 8` (không phải chuyển đổi gì vì
`Kind` mặc định `Device`, nhưng phải có nhánh để `CurrentVersion` tăng hợp lệ).

### Trạng thái bền vững

Hai file, tách rõ vai trò:

**(1) Ảnh trạng thái sống** — `<DataRoot>/hino.energy.json`, ghi bằng `DurableAtomicFile.WriteAllText`
(temp + fsync + `File.Replace`), **khoá theo storage key**. Mốc lưu **theo tag nguồn**, không theo tag
kết quả — vì `Day` và `PreviousDay` dùng chung mốc, và thêm một tag mới trên nguồn có sẵn là dùng
lại được ngay:

```json
{ "Format": "hino-energy", "SavedAt": "...",
  "Sources": { "<sourceStorageKey>": {
      "LastRaw": 12345678, "LastEffective": 123456.78, "LastAt": "...", "Wraps": 0,
      "Boundaries": {
        "Day":   { "CurrentStart": "2026-08-14T00:00:00+07:00", "CurrentBaseline": 123044.28,
                   "CurrentQuality": "Good", "CurrentSource": "live",
                   "PrevStart": "2026-08-13T00:00:00+07:00", "PrevBaseline": 122631.78, "PrevQuality": "Good" },
        "Month": { … }, "Year": { … } } } } }
```

Nhịp ghi: **debounce 1 s** cho `LastRaw`/`LastAt` (đúng cơ chế `RetainedValueStore`), nhưng **ghi
đồng bộ ngay lập tức** khi sang kỳ / phát hiện reset / rebase — đó là những khoảnh khắc duy nhất mà
mất 1 giây là mất luôn con số của cả ngày.

**(2) Nhật ký kỳ đã đóng** — `<DataRoot>/energy/energy-YYYYMM.jsonl`, append-only theo khuôn
`AuditTrail`, retention dài (mặc định **24 tháng**). Mỗi kỳ đóng ghi một dòng, kèm cả sự kiện bất
thường (`counter-reset`, `rollover`, `gap`, `spike-rejected`):

```json
{"t":"2026-08-14T00:00:00+07:00","event":"period-closed","source":"Inverter/Inv1Energy","key":"<guid>",
 "period":"Day","start":"2026-08-13T00:00:00+07:00","end":"2026-08-14T00:00:00+07:00",
 "startRaw":122631.78,"endRaw":123044.28,"energy":412.50,"quality":"Good","note":null}
```

Đây là thứ đem ra đối soát với Điện lực, và là chỗ tra "sản lượng ngày 12/8" mà không cần database.

### Thuật toán mỗi nhịp (mặc định 1 s)

1. `now = clock()`. **Kỷ luật đồng hồ**: nếu `now < LastAt − 5 s` → đồng hồ nhảy lùi (IPC hết pin RTC,
   NTP chỉnh sau khi boot) → log một lần, **không sang kỳ, không cập nhật mốc**, kết quả đánh
   `Uncertain` cho tới khi đồng hồ vượt lại `LastAt`.
2. Đọc `TagStore.TryGet(sourcePath)`. Nếu **không Good** → **giữ nguyên giá trị cũ**, đánh `Uncertain`
   kèm `StatusText` (tuyệt đối không trả 0 — 0 trên IOA 3 là điều độ đọc thành "nhà máy không phát").
3. Chuẩn hoá số đọc:
   - `raw < LastRaw`: nếu `RolloverAt > 0` và `(RolloverAt − LastRaw + raw)` là mức tăng hợp lý theo
     `MaxRatePerHour` → **wrap**, `Wraps++`; ngược lại → **counter reset** (thay thiết bị / nạp lại PLC):
     rebase toàn bộ mốc về `raw`, ghi nhật ký `counter-reset`, kỳ hiện tại đánh `Uncertain`.
   - `effective = raw + Wraps × RolloverAt`.
   - Chặn gai: nếu `MaxRatePerHour` bật và mức tăng vượt ngưỡng → **bỏ mẫu**, đếm `spike-rejected`,
     không cập nhật `LastRaw` (mốc không bao giờ lấy từ mẫu không Good hoặc mẫu bị loại).
4. Với từng loại kỳ (Day/Month/Year): `start = Boundary(now)` theo **giờ địa phương**. Nếu
   `start > CurrentStart` → **sang kỳ**: `Prev ← Current`, `Current ← { start, baseline, quality }`.
   Giá trị mốc lấy theo thứ tự:
   1. Gateway đang chạy liên tục (phát hiện sang kỳ trong vòng ≤2 nhịp) → `baseline = effective`, **Good**.
   2. Có mẫu trong lịch sử cách mốc ≤ `BoundaryToleranceSeconds` (mặc định 120) → **Good**.
   3. **Mẫu trước khoảng trống bằng mẫu sau khoảng trống** ⇒ counter không nhúc nhích trong lúc
      gateway chết ⇒ mốc chính là giá trị đó, **Good**. *(Ban đêm điện mặt trời luôn rơi vào ca này —
      gateway tắt 20h hôm trước tới 6h hôm sau vẫn ra số đúng tuyệt đối.)*
   4. Ngược lại → mẫu gần nhất trước mốc, **Uncertain** + ghi lý do (đúng lựa chọn của người dùng).
   5. Không có gì (mất điện dài hơn 7 ngày) → `baseline = effective`, **Uncertain**, nhật ký `gap`.
   Mỗi kỳ đóng ghi một dòng nhật ký. Chết nhiều ngày thì đóng lần lượt từng kỳ bị bỏ lỡ.
5. Xuất kết quả: `Day = effective − Day.CurrentBaseline`, `PreviousDay = Day.CurrentBaseline −
   Day.PrevBaseline`, tương tự Month/Year. Quality = **xấu nhất** trong (quality nguồn, quality mốc) —
   đúng quy tắc lan truyền chất lượng đã chốt của expression engine.
6. Publish **một** `Tags.Apply(batch)` cho cả lượt.

### Khởi động lại — điểm mấu chốt

Ngay khi nạp state (trước cả lần đọc thiết bị đầu tiên):
- `PreviousDay`/`PreviousMonth`/`PreviousYear` **đã xác định hoàn toàn** từ hai mốc đã lưu →
  publish **Good** ngay lập tức. Nghĩa là **IOA 3 (Ainv_D-1) đúng ngay từ mili-giây đầu tiên sau khi
  bật lại** — không có khoảng trống, không có số 0 gửi lên điều độ.
- Kỳ hiện tại publish giá trị cũ đã lưu, đánh `Uncertain` + `StatusText` "đang khôi phục sau khởi động
  lại", chuyển sang Good ngay ở lần đọc Good đầu tiên.

### Nơi chạy

`src/HinoGateway.Engine/AccumulatorEngine.cs` — **một producer độc lập**, không nhét vào
`InternalTagEngine` (614 dòng, đã đủ phức tạp với topological sort). Chạy trên `WorkerThread` riêng
(`WorkerThread.Run("accumulators", …)`), dựng trong `DeviceEngine.BuildAsync` cạnh `InternalTagEngine`,
nhận `Func<DateTimeOffset>? clock = null` để test không phải `Thread.Sleep` (đúng khuôn `AlarmEngine`).

Cân nhắc đã loại: **không** dùng `TagStore.AddObserver`. Observer chạy đồng bộ trên chính luồng poll,
và ở đây nó không cần thiết — kết quả là một phép trừ trên giá trị mới nhất nên bỏ lỡ mẫu trung gian
không hề tốn gì; thêm nữa gọi `Tags.Apply` từ trong observer của `Apply` là đệ quy vào chính nó. Đọc
`TagStore.TryGet` từ vòng lặp riêng vừa đơn giản hơn vừa không đụng vào đường poll nóng.

**Bẫy định tuyến đã kiểm chứng bằng mã, phải xử lý cùng lúc:** `JsonProjectStore.cs:448` lọc tag nội
bộ bằng `Kind.IsInternal()` rồi truyền vào `DeviceEngine.LoadAsync/ReloadAsync`, và
`DeviceEngine.BuildAsync` (dòng 343-346) đưa **toàn bộ** danh sách đó vào `InternalTagEngine.Load`,
nơi `InternalTagEngine.cs:101` từ chối mọi kind lạ. Nghĩa là chỉ cần thêm `Accumulator` vào
`IsInternal()` là accumulator sẽ bị nhét nhầm vào expression engine. Sửa đồng thời: `IsInternal()` bao
gồm `Accumulator` (nó đúng là tag không có connection/address/scaling), `IsWritable()` **không**, và
`BuildAsync` tách danh sách theo kind — `Memory|Expression` → `InternalTagEngine`, `Accumulator` →
`AccumulatorEngine`.

**`AccumulatorStateStore` phải là singleton DI** (như `RetainedValueStore`), không thuộc `Runtime` —
nếu không, mỗi lần bấm "Apply to engine" là `DeviceEngine.ApplyAsync` dựng lại `Runtime` và trạng thái
trong RAM biến mất. Engine rehydrate từ store sau mỗi lần reload.

Expression tag muốn cộng ba inverter (`TotalEnergyYesterday`) thì đọc qua `TagStore` như mọi tag khác,
trễ tối đa một nhịp (1 s) — chấp nhận được, và tránh phải trộn hai bộ định thứ tự.

### Dọn cái bẫy `Monotonic`

`TagDefinition.Monotonic` được khai báo, lưu, hiện trên UI và **mô tả đúng cho thanh ghi năng lượng**
— nhưng `grep` cho thấy **không có một dòng nào thực thi**. Để nguyên là để lại một cái bẫy: người
vận hành bật nó lên và tin rằng counter đã được bảo vệ. Đợt này cài thật trong
`DeviceWorker.ApplyScaling`: số đọc thấp hơn giá trị cao nhất đã nhận → **đánh `Uncertain` cho riêng
tag đó** kèm lý do, **trừ khi thấp hơn 3 lần liên tiếp** thì chấp nhận như một lần reset thiết bị
(nếu không, thay inverter là tag chết vĩnh viễn). Accumulator không dựa vào cờ này — nó tự xử lý
wrap/reset vì có đủ ngữ cảnh `RolloverAt`/`MaxRatePerHour`.

### API

`src/HinoGateway.Server/Controllers/EnergyController.cs` (mới):
- `GET /api/energy/state` — mốc hiện tại, chất lượng, nguồn mốc, thời điểm lấy mốc
- `GET /api/energy/periods?source=&period=&from=&to=` — đọc nhật ký kỳ đã đóng (nền cho UI đợt sau và
  cho báo cáo đối soát EVN)
- `GET /api/history?tags=&from=&to=` — đọc lịch sử thô

---

## Deliverable C — MQTT hai làn: realtime ưu tiên tuyệt đối, truyền bù tiết lưu

### Chẩn đoán gốc

Hiện `MqttPublisher` ghi **mọi** batch xuống spool rồi gửi FIFO đầu hàng, không tiết lưu, không ưu
tiên. Backlog 1 giờ = 720 batch chặn đầu hàng dữ liệu mới. Trần 256 MB khiến độ trễ tối đa lên tới
hàng chục phút.

### Làn 1 — realtime (sửa `MqttPublisher`)

Giữ nguyên kiến trúc collector → flusher → sender (đã tốt, đã test, backoff reset đúng chỗ), sửa hai điểm:
- `MaxSpoolBytes` mặc định **256 MB → 16 MB**: backlog tự chặn ở mức vài phút.
- Thêm `MaxLiveBatchAgeSeconds` (mặc định **300**): khi sender lấy batch ở đầu hàng mà envelope
  `Timestamp` đã quá hạn → **bỏ, đếm `LiveStaleDropped`**, không gửi. Dữ liệu đó không mất — làn 2 sẽ
  bù từ lịch sử. Đây chính là chỗ cắt đứt head-of-line blocking: sau khi broker trở lại, làn realtime
  hội tụ về dữ liệu hiện tại **trong vài giây** bất kể mất kết nối bao lâu.

Bảo lưu `MqttDiskSpool` trong làn realtime (thay vì bỏ hẳn) vì nó vẫn cứu được các cú rớt mạng ngắn
vài giây mà lịch sử 1 phút không diễn tả nổi; với trần 16 MB thì "hai bản sao bền vững" không còn là
lãng phí đáng kể.

### Làn 2 — truyền bù (`src/HinoGateway.Northbound.Mqtt/MqttBackfillPublisher.cs`, mới)

- **Client MQTT riêng**, clientId `<ClientId>-hist`. Hai client thay vì chia sẻ một client dưới lock:
  một lệnh publish bù đang bay không bao giờ chặn được lệnh publish realtime, và mỗi làn có
  backoff/flow-control độc lập.
- Đọc `ISampleHistory` từ **watermark bền vững** `<DataRoot>/mqtt/backfill.ack`
  (`DurableAtomicFile`, chứa timestamp frame cuối đã được ACK).
- Gói `BackfillFramesPerMessage` (mặc định 5) frame vào một bản tin, publish QoS 1 lên
  `BackfillTopic` (mặc định `<PublishTopic>/history`), payload mang `stream:"backfill"`, `seq`,
  `gateway` và timestamp gốc từng frame → server chống trùng theo `(gateway, tag, timestamp)`.
- **Tiết lưu** bằng token bucket, tái dùng đúng khuôn `Iec104Server.PointRateLimiter` (cấp phát từng
  phần, không chặn cả lô): `BackfillMaxMessagesPerSecond` mặc định **2**.
- **Nhường tuyệt đối cho realtime**: trước mỗi lần publish, nếu làn realtime còn hàng chờ
  (`spool.PendingBatches > 0`) hoặc chưa kết nối → bỏ nhịp. Realtime luôn đi trước.
- Watermark **chỉ tiến sau PUBACK**. Lần đầu chạy: watermark = `now − BackfillMaxAgeHours`
  (mặc định **0** = bắt đầu từ hiện tại, không đổ 7 ngày lịch sử lên server ngay lần đầu bật).
- Mất kết nối lâu hơn retention → watermark nhảy tới frame cũ nhất còn giữ và publish một bản tin
  `gap` để server biết đoạn nào thực sự mất, thay vì im lặng.

### Đo lường

`MqttPublisherStatus` thêm: `BackfillConnected`, `BackfillWatermark`, `BackfillLagSeconds`,
`BackfillMessages`, `BackfillGaps`, `LiveStaleDropped`. Component health mới: `mqtt-backfill`,
`history-sampler`. Thêm cổng cho `tests/HinoGateway.LoadTests`: `fullstack`/`chaos` phải giữ
`BackfillLagSeconds` hội tụ và `LiveStaleDropped` bằng 0 khi không có sự cố.

### Config mới (`MqttPublisherOptions`)

| Khoá | Mặc định |
|---|---|
| `MaxSpoolBytes` | `16777216` (đổi từ 256 MB) |
| `MaxLiveBatchAgeSeconds` | `300` |
| `BackfillEnabled` | `true` |
| `BackfillTopic` | `<PublishTopic>/history` |
| `BackfillMaxMessagesPerSecond` | `2` |
| `BackfillFramesPerMessage` | `5` |
| `BackfillMaxAgeHours` | `0` |

---

## Thứ tự thực hiện

**A → B → C.** A là nền của cả hai (mốc 0h của B, nguồn dữ liệu của C) và tự nó đã đóng một mục bắt
buộc của QĐ 378. B là hạng mục chặn nghiệm thu EVN. C giải bài lag.

Commit + `git push` sau mỗi chặng — repo tồn tại chính vì sự cố mất nguồn 10/08.

## Kiểm chứng

**Test tự động** (`tests/HinoGateway.Engine.Tests`, đặt tên kiểu câu như nhà đang dùng, đồng hồ tiêm
qua `Func<DateTimeOffset>`, thư mục tạm dưới `Path.GetTempPath()`):

- `A_frame_survives_a_torn_write_and_retention_never_deletes_the_open_file`
- `Yesterday_is_exact_after_the_process_restarts_across_midnight`
- `A_gateway_that_was_off_all_night_still_gets_the_right_baseline_because_the_counter_did_not_move`
- `A_missing_boundary_sample_yields_Uncertain_and_never_a_wrong_number`
- `A_counter_that_wraps_at_the_UInt32_limit_does_not_produce_a_negative_day`
- `A_replaced_inverter_rebases_instead_of_reporting_a_huge_negative_energy`
- `A_backward_clock_jump_does_not_roll_the_day_twice`
- `Previous_day_is_published_Good_before_the_first_device_read_after_a_restart`
- `A_bad_source_freezes_the_total_instead_of_publishing_zero`
- `Accumulator_state_survives_an_apply_to_engine`
- `A_stale_live_batch_is_dropped_so_fresh_data_is_published_within_one_interval`
- `Backfill_never_publishes_while_the_live_lane_has_pending_batches`
- `The_backfill_watermark_only_advances_after_the_broker_acknowledges`

**Chứng minh end-to-end bằng cách chạy thật** (không chỉ đọc mã):

1. **Qua nửa đêm, có mất điện.** Chạy gateway với `Func<DateTimeOffset>` giả lập (hoặc profile load
   test riêng), bơm counter tăng đều, **kill tiến trình** trước 0h, khởi động lại sau 0h → kiểm tra
   `PreviousDay` khớp chính xác chênh lệch counter giữa hai mốc, và `/api/energy/periods` có đúng một
   dòng kỳ đã đóng với `quality: Good`.
2. **Mất điện xuyên đêm.** Tắt gateway 20h→6h với counter đứng yên → mốc 0h phải là **Good** nhờ quy
   tắc "trước = sau", không phải Uncertain.
3. **Trên PLC thật** (192.168.3.4, DB1/2/3[144]): tạo đủ bộ accumulator cho 3 inverter, đấu vào IOA
   3/14/16/18, bật IEC-104 nội bộ và dùng client lib60870 kiểm tra giá trị + cờ chất lượng NT/IV.
4. **Mất broker 1 giờ.** Chạy mosquitto local, publish bình thường, **tắt broker 60 phút**, bật lại →
   đo: làn realtime có bản tin tươi **trong vòng < 10 s**, và làn bù nhỏ giọt phía sau ở đúng
   ~2 bản tin/giây cho tới khi `BackfillLagSeconds` về 0; không mất frame nào trong 7 ngày retention.
5. **`soak`** 30 phút với accumulator + historian bật, để chốt rằng trạng thái mới không rò heap theo
   số lần reload (bộ đo managed-heap theo checkpoint đã có sẵn).

## Ngoài phạm vi đợt này (ghi rõ để khỏi hiểu nhầm)

- **UI** — người dùng chọn làm sau; đợt này chỉ có tag + API.
- **Phương pháp `Integrate`** (tích phân kW→kWh cho thiết bị không có counter): để lại một seam
  `IAccumulationMethod` nhưng không cài. Thiết bị hiện tại đều có thanh ghi cộng dồn, và tích phân là
  cách duy nhất mà một mẫu lỗi gây sai vĩnh viễn — chỉ nên làm khi thật sự có thiết bị cần.
- **Nhập tay/sửa mốc trên UI** — chưa; nhật ký kỳ đã đóng đủ để đối soát thủ công trước mắt.

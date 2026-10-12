using System.Buffers.Binary;

namespace S9Battery.Core;

// E HUB 3.3.2, Beiying v2.1. Only these three read commands are exposed.
public enum Query { Identity, Battery, Online }
public enum BatterySource { Feature, Receiver, DeviceEvent, WirelessEvent }
public record BatteryReading(int Level, bool Charging, BatterySource Source = BatterySource.Feature)
{
    // Captured wired data reports charging + 100 while wireless still reports 78.
    // Preserve the raw number for diagnostics, but don't present it as measured charge.
    public bool HasReliableLevel => Source == BatterySource.Receiver || !Charging;
    public bool HasDisplayLevel => HasReliableLevel || Source == BatterySource.DeviceEvent && Level < 100;
}

public static class Protocol
{
    public const ushort VendorId = 0x372E;
    public const ulong S9ProIdentity = 0x09000000000E;
    public const byte ReportId = 3;
    public static bool IsCandidate(ushort vendor, ushort product) =>
        vendor == VendorId && product is 0x1029 or 0x1026 or 0x103D;
    public static bool IsWireless(ushort product) => product is 0x1026 or 0x103D;

    public static byte[] Encode(Query query, int featureLength = 64)
    {
        if (featureLength < 64 || featureLength > 4096)
            throw new ArgumentOutOfRangeException(nameof(featureLength));
        var packet = new byte[featureLength];
        packet[0] = ReportId; // Windows requires the ID before the 63-byte SDK request.
        packet[2] = 0x50;
        packet[5] = query == Query.Online ? (byte)0x47 : (byte)0x4F;
        packet[6] = Offset(query);
        packet[1] = unchecked((byte)(packet[2] + packet[5] + packet[6]));
        return packet;
    }

    public static byte Offset(Query query) => query switch
    {
        Query.Identity => 0x80, Query.Battery => 0x81, Query.Online => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(query))
    };

    // The firmware clears the read bit (0x80) when reporting the returned offset.
    public static byte ResponseOffset(Query query) => query == Query.Online ? (byte)0x47 :
        (byte)(Offset(query) & 0x7F);

    // Unlike input reports, WebHID feature responses retain the report ID.
    // These offsets therefore apply directly to the native Windows response.
    public static byte[] Decode(Query query, ReadOnlySpan<byte> report)
    {
        if (report.Length < 6 || report[0] != ReportId || report[2] != 0 ||
            report[4] != 0 || report[5] != ResponseOffset(query))
            throw new InvalidDataException("设备响应不匹配（report ID、ACK、分包或查询偏移）。");
        int length = report[3];
        int minimum = query switch { Query.Identity => 8, Query.Battery => 7, _ => 1 };
        // The SDK's ten-byte packet limit is an encoding default, not a response limit.
        // Real S9 Pro firmware returns eleven-byte identity and basic-info payloads.
        if (length < minimum || report.Length < 6 + length)
            throw new InvalidDataException("设备响应数据长度无效。");
        return report.Slice(6, length).ToArray();
    }

    public static ulong ParseIdentity(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 8) throw new InvalidDataException("设备标识响应过短。");
        return ((ulong)BinaryPrimitives.ReadUInt32BigEndian(payload[2..6]) << 16) |
            BinaryPrimitives.ReadUInt16BigEndian(payload[6..8]);
    }

    public static BatteryReading ParseBattery(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 7) throw new InvalidDataException("电量响应过短。");
        int level = payload[0] & 0x7F;
        if (level > 100) throw new InvalidDataException($"无效电量：{level}。");
        return new(level, (payload[0] & 0x80) != 0);
    }

    public static bool ParseOnline(ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1 || payload[0] > 1)
            throw new InvalidDataException("无线在线状态无效。");
        return payload[0] == 1;
    }

    public static int? ParseBatteryEvent(ReadOnlySpan<byte> nativeReport)
    {
        // Windows input includes ID; WebHID strips it before parsing FA / event 05 / level.
        // Never interpret mouse movement, buttons, DPI changes or feature responses as battery.
        // The official input handler does not require the input ID to match feature ID 03.
        if (nativeReport.Length < 4 || nativeReport[1] != 0xFA || nativeReport[2] != 5 || nativeReport[3] > 100)
            return null;
        return nativeReport[3];
    }
}

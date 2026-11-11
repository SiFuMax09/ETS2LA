using ETS2LA.Shared;
using ETS2LA.Logging;
using ETS2LA.Backend.Events;
using static ETS2LA.Translations.T;

using System.Numerics;
using System.Diagnostics;
using System.IO.MemoryMappedFiles;

namespace ETS2LA.Game.SDK;

public class DynamicObject : BaseVehicle
{
    public int id;

    /// <summary>
    /// ETrafficObjectType value from the game (see the plugin's traffic_object.hpp).
    /// </summary>
    public int type;

    /// <summary>
    /// Speed in m/s, 0 if the game doesn't expose it for this object type.
    /// </summary>
    public float speed;
}

public class DynamicObjectData
{
    public required List<DynamicObject> objects;
}

/// <summary>
/// Reads the moving objects that the game plugin exports to ETS2LADynamicObjects.
/// Layout: 64 slots * 52 bytes, no header. Unused slots have type == 0 and are skipped.
/// </summary>
public class DynamicObjectsProvider
{
    private static readonly Lazy<DynamicObjectsProvider> _instance = new(() => new DynamicObjectsProvider());
    public static DynamicObjectsProvider Current => _instance.Value;

    private float UpdateRate { get; set; } = 1f / 60f;
    public string EventString = "ETS2LA.Game.SDK.DynamicObjects.Data";

    private MemoryReader _reader;
    private DynamicObjectData? _currentData;

    string mmapName = "Local\\ETS2LADynamicObjects";
    string mmapNameLinux = "/dev/shm/ETS2LADynamicObjects";

    const int SlotCount = 64;
    const int SlotSize = 52;
    int mmapSize = SlotCount * SlotSize; // 3328

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private byte[] _buffer = Array.Empty<byte>();
    private readonly Stopwatch _sinceReconnect = Stopwatch.StartNew();

    public DynamicObjectsProvider()
    {
        _buffer = new byte[mmapSize];
        _reader = new MemoryReader(_buffer);

        Thread updateThread = new Thread(UpdateThread)
        {
            IsBackground = true
        };
        updateThread.Start();
    }

    public DynamicObjectData? GetCurrentDynamicObjectData()
    {
        return _currentData;
    }

    private void UpdateThread()
    {
        Stopwatch stopwatch = new Stopwatch();
        stopwatch.Start();

        while (true)
        {
            int timeLeft = (int)((UpdateRate * 1000) - stopwatch.Elapsed.TotalMilliseconds);
            if (timeLeft > 0)
            {
                Thread.Sleep(timeLeft);
                continue;
            }

            stopwatch.Restart();
            try { Update(); }
            catch (Exception ex)
            {
                Logger.Error(ex.ToString(), _("Error in dynamic objects update loop."));
            }
        }
    }

    private bool TryOpenMemory()
    {
        if (_accessor != null)
            return true;

        try
        {
            #if WINDOWS
                _mmf = MemoryMappedFile.OpenExisting(mmapName);
            # else
                _mmf = MemoryMappedFile.CreateFromFile(mmapNameLinux);
            # endif

            _accessor = _mmf.CreateViewAccessor(0, mmapSize, MemoryMappedFileAccess.Read);
            return true;
        }
        catch (FileNotFoundException)
        {
            CloseMemory();
            Thread.Sleep(10000);
            return false;
        }
        catch (Exception ex)
        {
            CloseMemory();
            Logger.Error(_("Error initializing memory mapped file: {0}", ex.Message));
            Thread.Sleep(10000);
            return false;
        }
    }

    private void CloseMemory()
    {
        _accessor?.Dispose();
        _accessor = null;
        _mmf?.Dispose();
        _mmf = null;
    }

    private void Update()
    {
        if (_currentData == null)
        {
            _currentData = new DynamicObjectData { objects = new List<DynamicObject>() };
        }

        if (!TryOpenMemory())
            return;

        try
        {
            _accessor!.ReadArray(0, _buffer, 0, mmapSize);
        }
        catch (Exception)
        {
            // Mapping went away (e.g. game closed), reconnect on the next update.
            CloseMemory();
            return;
        }

        List<DynamicObject> objects = new List<DynamicObject>();
        for (int i = 0; i < SlotCount; i++)
        {
            int offset = i * SlotSize;

            // type == 0 marks an empty slot
            int type = _reader.ReadInt(offset + 48);
            if (type == 0)
                continue;

            DynamicObject obj = new DynamicObject();

            // 0
            obj.Position = new Vector3(
                _reader.ReadFloat(offset),
                _reader.ReadFloat(offset + 4),
                _reader.ReadFloat(offset + 8)
            );

            // 12, the plugin writes w, x, y, z. This is read in the same positional
            // order as the traffic and parked providers so the vision boxes line up.
            obj.Rotation = new Quaternion(
                _reader.ReadFloat(offset + 12),
                _reader.ReadFloat(offset + 16),
                _reader.ReadFloat(offset + 20),
                _reader.ReadFloat(offset + 24)
            );

            // 28
            obj.Size = new Vector3(
                _reader.ReadFloat(offset + 28), // Width
                _reader.ReadFloat(offset + 32), // Height
                _reader.ReadFloat(offset + 36)  // Length
            );

            // 40
            obj.speed = _reader.ReadFloat(offset + 40);
            obj.id = _reader.ReadInt(offset + 44);
            obj.type = type;

            objects.Add(obj);
        }

        _currentData.objects = objects;
        Events.Current.Publish<DynamicObjectData>(EventString, _currentData);

        // Periodically reopen the mmap to detect game restarts.
        if (_sinceReconnect.Elapsed.TotalSeconds > 1.0)
        {
            CloseMemory();
            _sinceReconnect.Restart();
        }
    }
}

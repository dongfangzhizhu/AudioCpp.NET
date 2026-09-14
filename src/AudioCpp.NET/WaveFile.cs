namespace AudioCpp.NET;

public static class WaveFile
{
    public static AudioBuffer Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.OpenRead(path);
        return Read(stream);
    }

    public static AudioBuffer Read(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        if (new string(reader.ReadChars(4)) != "RIFF") throw new InvalidDataException("WAV must use RIFF format.");
        reader.ReadInt32();
        if (new string(reader.ReadChars(4)) != "WAVE") throw new InvalidDataException("Not a WAVE file.");

        short format = 0, channels = 0, bits = 0;
        int sampleRate = 0;
        byte[]? data = null;
        while (stream.Position + 8 <= stream.Length)
        {
            var id = new string(reader.ReadChars(4));
            var size = reader.ReadInt32();
            if (size < 0 || stream.Position + size > stream.Length) throw new InvalidDataException("Invalid WAV chunk.");
            if (id == "fmt ")
            {
                if (size < 16) throw new InvalidDataException("Invalid WAV format chunk.");
                format = reader.ReadInt16(); channels = reader.ReadInt16(); sampleRate = reader.ReadInt32();
                reader.ReadInt32(); reader.ReadInt16(); bits = reader.ReadInt16();
                stream.Position += size - 16;
            }
            else if (id == "data") data = reader.ReadBytes(size);
            else stream.Position += size;
            if ((size & 1) != 0 && stream.Position < stream.Length) stream.Position++;
        }
        if (format != 1 || bits != 16 || channels <= 0 || sampleRate <= 0 || data is null)
            throw new InvalidDataException("Only PCM16 WAV files are supported.");
        var samples = new float[data.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BitConverter.ToInt16(data, i * 2) / 32768f;
        return new AudioBuffer(samples, sampleRate, channels);
    }

    public static void Write(string path, AudioBuffer audio)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        Write(stream, audio);
    }

    public static void Write(Stream stream, AudioBuffer audio)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(audio);
        if (audio.Channels <= 0 || audio.SampleRate <= 0) throw new ArgumentException("Invalid audio format.", nameof(audio));
        using var writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
        var dataSize = checked(audio.Samples.Length * 2);
        writer.Write("RIFF"u8); writer.Write(checked(36 + dataSize)); writer.Write("WAVE"u8);
        writer.Write("fmt "u8); writer.Write(16); writer.Write((short)1); writer.Write((short)audio.Channels); writer.Write(audio.SampleRate);
        writer.Write(checked(audio.SampleRate * audio.Channels * 2)); writer.Write((short)(audio.Channels * 2)); writer.Write((short)16);
        writer.Write("data"u8); writer.Write(dataSize);
        foreach (var sample in audio.Samples.Span)
            writer.Write((short)Math.Clamp(sample * 32767f, short.MinValue, short.MaxValue));
    }
}

public static class ModelDirectory
{
    public static string Default
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("AUDIOCPP_MODELS_DIR");
            if (!string.IsNullOrWhiteSpace(configured)) return Path.GetFullPath(configured.Trim());
            var root = OperatingSystem.IsWindows()
                ? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)
                : Environment.GetEnvironmentVariable("XDG_DATA_HOME") ??
                  Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(root, "audiocpp.net", "models");
        }
    }
}
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;

namespace Controller.Common;

public interface IRetainDataService
{
    T GetValue<T>(string key, T defaultValue);
    void SetValue<T>(string key, T value);
}

/// <summary>
/// 掉电保持数据服务
/// </summary>
public class JsonRetainDataService : IRetainDataService
{
    private readonly string _filePath = "RetainData.json";
    private readonly ConcurrentDictionary<string, JsonElement> _data;
    private readonly object _fileLock = new();

    public JsonRetainDataService(ILogger<JsonRetainDataService> logger)
    {
        _data = new ConcurrentDictionary<string, JsonElement>();

        // 启动时加载历史数据
        if (File.Exists(_filePath))
        {
            try
            {
                string json = File.ReadAllText(_filePath);
                _data = JsonSerializer.Deserialize<ConcurrentDictionary<string, JsonElement>>(json)
                        ?? new ConcurrentDictionary<string, JsonElement>();
            }
            catch (Exception ex)
            {
                logger.LogError($"加载 RetainData 失败，将使用默认值: {ex.Message}");
            }
        }
    }

    public T GetValue<T>(string key, T defaultValue)
    {
        if (_data.TryGetValue(key, out JsonElement element))
        {
            try
            {
                return element.Deserialize<T>() ?? defaultValue;
            }
            catch
            {
                return defaultValue;
            }
        }
        return defaultValue;
    }

    public void SetValue<T>(string key, T value)
    {
        // 将新值序列化为 JsonElement 存入字典
        var jsonString = JsonSerializer.Serialize(value);
        var element = JsonSerializer.Deserialize<JsonElement>(jsonString);

        _data[key] = element;

        // 异步触发保存，防止 I/O 阻塞 10ms 的控制主循环
        Task.Run(() => SaveToFile());
    }

    private void SaveToFile()
    {
        lock (_fileLock)
        {
            try
            {
                // 写入临时文件，再原子替换，防止写到一半断电导致整个文件损坏
                string tempPath = _filePath + ".tmp";
                string json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(tempPath, json);
                File.Move(tempPath, _filePath, true);
            }
            catch
            {
                // 忽略写入冲突
            }
        }
    }
}

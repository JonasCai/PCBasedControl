

using Common.Licensing;

Console.WriteLine("1 - 生成密钥对");
Console.WriteLine("2 - 根据设备请求文件生成授权文件");
Console.Write("请选择: ");
string? input = Console.ReadLine();

switch (input)
{
    case "1":
        GenerateKeys();
        break;
    case "2":
        GenerateLicenseFromRequest();
        break;
    default:
        Console.WriteLine("无效选择");
        break;
}


static void GenerateKeys()
{
    SecurityGuard.GenerateKeys(out string publicKey, out string privateKey);

    File.WriteAllText("public.key.txt", publicKey);
    File.WriteAllText("private.key.txt", privateKey);

    Console.WriteLine("已生成:");
    Console.WriteLine(Path.GetFullPath("public.key.txt"));
    Console.WriteLine(Path.GetFullPath("private.key.txt"));
    Console.WriteLine();
    Console.WriteLine("把 public.key.txt 内容复制到客户端 LicenseManager 的 PUBLIC_KEY 常量里。");
}

static void GenerateLicenseFromRequest()
{
    Console.Write("设备请求文件路径(.request.json): ");
    string? requestPath = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(requestPath) || !File.Exists(requestPath))
    {
        Console.WriteLine("文件不存在");
        return;
    }

    Console.Write("私钥文件路径(private.key.txt): ");
    string? privateKeyPath = Console.ReadLine();

    if (string.IsNullOrWhiteSpace(privateKeyPath) || !File.Exists(privateKeyPath))
    {
        Console.WriteLine("私钥文件不存在");
        return;
    }

    string requestJson = File.ReadAllText(requestPath);
    DeviceRequest request = DeviceRequestService.Deserialize(requestJson);

    string privateKey = File.ReadAllText(privateKeyPath).Trim();

    Console.WriteLine($"产品: {request.ProductCode}");
    Console.WriteLine($"客户: {request.CustomerName}");
    Console.Write("授权客户名(可回车沿用请求中的): ");
    string? customerName = Console.ReadLine();
    if (string.IsNullOrWhiteSpace(customerName))
        customerName = request.CustomerName;

    Console.Write("有效天数(例如 365): ");
    if (!int.TryParse(Console.ReadLine(), out int days) || days <= 0)
    {
        Console.WriteLine("有效天数无效");
        return;
    }

    Console.Write("功能列表，逗号分隔(例如 Core,Export,AdvancedReport): ");
    string? featureLine = Console.ReadLine();
    string[] features = string.IsNullOrWhiteSpace(featureLine)
        ? Array.Empty<string>()
        : featureLine.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    var license = new LicenseModel
    {
        LicenseId = Guid.NewGuid().ToString("N"),
        ProductCode = request.ProductCode,
        CustomerName = customerName ?? string.Empty,
        IssuedAtUtc = DateTime.UtcNow,
        ExpireDateUtc = DateTime.UtcNow.AddDays(days),
        Features = features,
        DeviceBinding = request.DeviceBinding
    };

    string licenseText = LicenseGenerator.CreateLicenseFile(license, privateKey);

    string outPath = Path.ChangeExtension(requestPath, ".lic");
    File.WriteAllText(outPath, licenseText);

    Console.WriteLine("授权文件已生成:");
    Console.WriteLine(Path.GetFullPath(outPath));
}
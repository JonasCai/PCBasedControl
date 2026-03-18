using Controller;
using Controller.EventLogger;
using Controller.gRPC;
using Controller.Hardware;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
{
    // 定义一个全局唯一的管道名称
    string pipeName = "S88_Control_Engine_Pipe";

    // 监听命名管道 (Windows 首选)
    options.ListenNamedPipe(pipeName, listenOptions =>
    {
        // gRPC 必须使用 HTTP/2
        listenOptions.Protocols = HttpProtocols.Http2;
    });

    /* // 如果你是 Linux (Ubuntu/Debian) 或 Windows 10/11 较新版本，也可以用 UDS：
    var socketPath = Path.Combine(Path.GetTempPath(), "s88_control.sock");
    options.ListenUnixSocket(socketPath, listenOptions =>
    {
        listenOptions.Protocols = HttpProtocols.Http2;
    });
    */
});

// 绑定 appsettings.json 中的配置项
builder.Services.Configure<MongoDbSettings>(
    builder.Configuration.GetSection("MongoDbSettings"));

builder.Services.AddSingleton<HwData>();
builder.Services.AddSingleton<EventLoggerService>();
builder.Services.AddSingleton<IEventRepository, EventRepository>();

builder.Services.AddHostedService<ControlLoopWorker>();
builder.Services.AddGrpc(); // 注册 gRPC 引擎
var app = builder.Build();
app.MapGrpcService<HmiServiceImpl>();// 将 HmiServiceImpl 暴露给外部网络
app.Run();

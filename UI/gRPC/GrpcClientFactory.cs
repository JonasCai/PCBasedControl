using Grpc.Net.Client;
using Shared.HmiService;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace UI.gRPC
{
    public class GrpcClientFactory
    {
        public static HmiService.HmiServiceClient CreatePipeClient()
        {
            string pipeName = "S88_Control_Engine_Pipe";

            // 1. 自定义 HTTP 处理器的底层连接逻辑
            var handler = new SocketsHttpHandler
            {
                ConnectCallback = async (context, cancellationToken) =>
                {
                    // 创建命名管道客户端流
                    var clientStream = new NamedPipeClientStream(
                        serverName: ".", // "." 代表本机
                        pipeName: pipeName,
                        direction: PipeDirection.InOut,

                        // 【致命细节】：必须开启 Asynchronous 和 WriteThrough，
                        // 否则 gRPC 的 HTTP/2 多路复用会在这里死锁或严重掉帧！
                        options: PipeOptions.Asynchronous | PipeOptions.WriteThrough);

                    try
                    {
                        // 尝试连接服务端 (可在此处设置超时时间)
                        await clientStream.ConnectAsync(cancellationToken);
                        return clientStream;
                    }
                    catch
                    {
                        clientStream.Dispose();
                        throw;
                    }
                }
            };

            // 2. 创建 gRPC Channel
            // 注意：这里的 URL 是个占位符，因为底层的 ConnectCallback 已经被我们拦截接管了，
            // 所以写 http://localhost 只是为了满足 Uri 的格式要求。
            var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
            {
                HttpHandler = handler
            });

            // 3. 返回强类型的客户端
            return new HmiService.HmiServiceClient(channel);
        }
    }
}

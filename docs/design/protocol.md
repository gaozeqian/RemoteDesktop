# 远程桌面通信协议规范

本项目属于远程控制软件，没有传统意义上的 "RESTful API"，其核心 API 就是**网络通信协议**。
整个协议栈分为三层：信令层（Rendezvous）、传输层（Transport）与应用层（Multiplexed Channels）。

## 一、 信令层 (Signaling)

信令用于让控制端和被控端发现彼此、交换公钥指纹、并协商 NAT 穿透方案。信令服务器本身**不接触加密后的桌面画面和按键**。

*   **协议**：WebSocket (`ws://` 或 `wss://`)
*   **端口**：默认 TCP 5080
*   **数据格式**：二进制，直接发送 Protobuf 序列化后的 Envelope (`ClientMessage` / `ServerMessage`)。
*   **Protobuf 定义**：`src/Core/RemoteDesktop.Signaling/signaling.proto`

**核心流程**：
1. **上线注册**：被控端连上 WebSocket 后发送 `Register`，包含其设备 ID 派生出的公钥指纹。服务器返回 `RegisterChallenge` 要求设备签名，验签通过后服务器回复 `Registered` 并分配短时连接密码。
2. **状态查询**：控制端可发送 `PresenceQuery`，询问一组设备是否在线。
3. **发起连接**：控制端发送 `ConnectRequest`，提供目标设备 ID 和自己的信息（IP/端口/中继支持等）。
4. **握手转发**：服务器将请求转化为 `IncomingConnection` 转发给被控端；被控端接受后返回 `ConnectAccept`。
5. **打洞与中继票据**：信令服务器为双方交换内网/公网 IP 列表（用于 NAT 打洞），并颁发**中继票据 (Relay Ticket)**，以备直连失败时使用。

## 二、 传输层 (Transport)

实际的音视频画面和控制指令直接在两端之间传输，或经过 Relay 服务器透明转发（Relay 看不到明文）。

*   **协议**：QUIC (基于 TLS 1.3 的可靠 UDP 传输协议)
*   **加密机制**：端到端加密（E2EE）。双方在 QUIC 握手时强制验证对方的证书指纹（通过信令层提前获取到的指纹），防御中间人攻击。
*   **穿透方式**：
    1.  **地址反射与直连 (Hole Punching)**：控制端向被控端发出的内网和公网 UDP 端口发送 UDP 探测包，尝试建立 P2P QUIC 连接。
    2.  **中继回退 (Relay Fallback)**：如果 P2P 失败，双方拿着信令服务器签发的 "中继票据" 连接到 Relay 服务器 (默认 UDP 5082)。Relay 服务器验证票据后，在内存中直接拼接双方的 UDP 数据包，充当哑管道。

## 三、 应用层 (Multiplexed Channels)

当 QUIC 连接建立后，应用层会基于 QUIC 的多路复用特性，为不同的业务功能打开多个独立的 Stream（单向或双向）。

*   **信道分配**：由 `ChannelType` 枚举定义，包含：
    *   0: `Control` (鉴权、版本协商、权限控制)
    *   1: `Video` (桌面画面，H.264/硬件编码)
    2.  `Audio` (Opus 编码音频环回)
    3.  `Input` (鼠标、键盘事件)
    4.  `Clipboard` (文本、图片剪贴板双向同步)
    5.  `FileTransfer` (文件传输块)
    6.  `Terminal` (ConPTY 字符流与大小重置命令)
    7.  `Camera` (远程摄像头数据帧)

*   **帧格式 (Wire Format)**：
    每一个应用层消息（除特定的视频或音频原始流外）都使用统一的结构包装（大端序）：
    ```
    [ uint32 payload_length ]  (4 bytes)
    [ uint16 message_type ]    (2 bytes)
    [ payload (protobuf) ]     (变长)
    ```

*   **Protobuf 定义**：所有应用层消息结构定义在 `src/Core/RemoteDesktop.Protocol/*.proto` 目录下。

### 鉴权过程
1. 在 `Control` 信道建立后，控制端必须立刻发送鉴权请求。
2. 密码绝证明文传输，而是使用 `HMAC(密码派生密钥, nonce ‖ 双方证书指纹)` 证明自己持有正确的连接密码。
3. 验证通过后，被控端依据策略（如是否为仅观看模式）开放其他信道的数据收发权限。

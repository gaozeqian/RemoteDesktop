# 签名

## 这个目录怎么放东西

签名是各平台各做各的，没有共用代码，所以按平台分文件夹；**跨平台的脚本放在本目录**：

| 位置 | 里面是什么 |
|---|---|
| `windows/` | 只在 Windows 上有意义的两件事：`New-SigningCertificate.ps1`（用 `New-SelfSignedCertificate` 生成自签名代码签名证书）和 `Install-TrustedCertificate.ps1`（把这台机器的证书存储改掉）。生成的证书落在 `windows/certificates/` |
| 本目录 | 跨平台的脚本。`New-ServerCertificate.ps1` 给信令服务器生成 TLS 证书，用的是纯 .NET API，因此在 **Linux/macOS 的 pwsh 上同样能跑**——服务端将来在那边部署时，Kestrel 用的就是它生成的 `.pfx` |
| `certificates/` | 生成的证书和私钥（已 gitignore，永远不进仓库） |

将来 macOS（`codesign` + `notarytool`）和 Linux（gpg / dpkg-sig）的签名脚本各自建文件夹，`windows/` 不动。
签名脚本的默认输出目录是"脚本自己旁边"，所以跨平台脚本的证书落在 `certificates/`，Windows 那两个的落在 `windows/certificates/`。

安装包和程序都需要**代码签名**：没有签名时 Windows SmartScreen 会提示"未知发布者"，企业策略和杀毒软件也更可能拦截。

这套构建脚本的签名是**自动**的：`build-installer.ps1` 会在本机和当前用户的证书存储里找可用的代码签名证书
（有私钥、未过期、用途是代码签名），找到就签名。它只自动使用**名字里含 `RemoteDesktop`** 的证书——别人的证书
（比如另一个产品的）绝不会被默默拿来签本产品；那种情况下脚本会列出候选并让你用 `-SignCertificate` 明确指定。

签名分两步，顺序在脚本里是固定的，因为顺序错了就白签：

1. **先签发布目录里本产品自己的 exe/dll**（`RemoteDesktop*.exe`、`RemoteDesktop*.dll`，当前是 18 个）。这样**装到机器上的程序**
   也是签过名的——SmartScreen 和杀毒软件看的是用户真正运行的那个 exe。旁边的 .NET 运行时和微软的库本来就有微软的签名，不动它们。
2. **再给 MSI 签名**。签名之后脚本会用 `signtool verify /pa`（或 PowerShell 的签名检查）验证一次。

## 用哪张证书

| 场景 | 做法 |
|---|---|
| 只是让构建跑通、本机测试 | `pwsh -File build/signing/windows/New-SigningCertificate.ps1` 生成自签名证书（名字符合本产品，会被自动选中）。**这种证书不能用于发布**：只在装过它的 `.cer` 的机器上有效。 |
| 正式发布（OV 证书） | 向 CA（DigiCert、Sectigo、GlobalSign 等）购买，通常一年几百到上千元。导入当前用户或本机证书存储后，脚本会自动找到；也可以用 `-SignCertificate <指纹或 CN>`。 |
| 正式发布（EV 证书） | 私钥在硬件令牌/HSM 里，导入后同样可用。EV 的好处是 SmartScreen 信誉立刻生效（OV 需要积累）。 |
| 不想管证书文件 | 用云签名服务（见下），私钥不落地。 |

自签名证书的删除：先取消信任（`Install-TrustedCertificate.ps1 -Remove`），再从证书存储里删掉
（`New-SigningCertificate.ps1 -RemoveFromStore`）。

## 只有 signtool 一条路吗

不是。脚本内置三种工具，用 `-SigningTool` 指定，默认 `auto`（按"最少配置"选）：

| 工具 | 凭证 | 平台 | 说明 |
|---|---|---|---|
| `signtool` | 证书存储里的证书（`-SignCertificate` 或自动查找）或 PKCS#12 文件 | Windows | Windows SDK 的 "Signing Tools"，最常用；支持硬件令牌与云 HSM |
| `powershell` | 证书存储里的证书，或 PKCS#12 文件 | Windows | 内置的 `Set-AuthenticodeSignature`，**不需要装 Windows SDK** |
| `osslsigncode` | **证书文件**：`-CertificateFile <pfx>`（+`-CertificatePassword`）或 `-CertificateFile <crt.pem> -PrivateKeyFile <key.pem>` | Windows / Linux / macOS | 开源跨平台，**能签 MSI**，适合 Linux CI 或没有 Windows SDK 的机器 |

`auto` 的选择顺序：

1. 给了 PKCS#12（`-CertificateFile <pfx>`）→ **PowerShell**。它是 Windows 自带的，不依赖机器上装没装
   Windows SDK、也不依赖 `signtool` 在不在 PATH 里；密码留在进程内部，不进入命令行（进程列表里谁都看得到）。
2. 否则存储里有可用证书 → `signtool`；没有 signtool 就用 PowerShell。
3. 只有 PEM 证书 + 私钥文件（`-CertificateFile <crt.pem> -PrivateKeyFile <key.pem>`）→ `osslsigncode`。

`-CertificateFile` 比存储里的证书更明确：两个都给时，用文件签。都没有就跳过并说明缺什么。

CI 上最常见的用法（证书以 Base64 放在 Secrets 里，解码成 .pfx 再传给脚本）：

```powershell
.\build\installer\client\windows\build-installer.ps1 -RendezvousUri ws://<服务器>:5080/ws `
    -CertificateFile .\sign.pfx -CertificatePassword $env:PFX_PASSWORD
```

### 用 osslsigncode 签名

```bash
# 安装：Windows 用 winget，Debian/Ubuntu 用 apt，macOS 用 brew
winget install MichalTrojnara.osslsigncode
sudo apt install osslsigncode
brew install osslsigncode
```

```powershell
# PKCS#12（.pfx/.p12）：-SingingTool 不必指定，Windows 上 auto 会用 PowerShell 签（不需要装 osslsigncode）
pwsh -File build/installer/client/windows/build-installer.ps1 `
    -RendezvousUri ws://<服务器>:5080/ws `
    -CertificateFile C:\keys\codesign.pfx -CertificatePassword <密码>

# 指定 osslsigncode 也可以（Linux/macOS 上只有它），密码也可以放在环境变量 RD_SIGNING_PASSWORD 里
pwsh -File build/installer/client/windows/build-installer.ps1 `
    -RendezvousUri ws://<服务器>:5080/ws `
    -SigningTool osslsigncode -CertificateFile C:\keys\codesign.pfx -CertificatePassword <密码>

# PEM 证书 + 私钥（从 CA 拿到的 .crt/.cer + .key，或从 pfx 导出）——这一种只有 osslsigncode 支持
pwsh -File build/installer/client/windows/build-installer.ps1 `
    -SigningTool osslsigncode -CertificateFile C:\keys\codesign.crt.pem -PrivateKeyFile C:\keys\codesign.key.pem
```

在 Linux/macOS 上，把脚本里的 `dotnet publish` 目标换成对应 RID、并跳过 WiX 那一步（WiX 只能在 Windows 上打 MSI），
也可以只用 osslsigncode 单独签已经打好的文件：

```bash
osslsigncode sign -h sha256 -n RemoteDesktop -ts http://timestamp.digicert.com \
  -pkcs12 codesign.pfx -pass "$PASS" -in RemoteDesktop-0.2.0.0-x64.msi -out signed.msi
osslsigncode verify -in signed.msi
```

从 pfx 导出 PEM 与私钥（OpenSSL）：

```bash
openssl pkcs12 -in codesign.pfx -clcerts -nokeys -out codesign.crt.pem   # 证书（含链）
openssl pkcs12 -in codesign.pfx -nocerts -nodes -out codesign.key.pem    # 私钥（未加密）
```

### 其他方案（需要自己接进构建）

- **Azure Trusted Signing**（微软的云签名服务）：不用购买和保管证书，按签名次数计费，signtool 可用 `/dlib` 调用，
  或直接用其 API。适合 CI。
- **云 KMS / Azure Key Vault / AWS KMS**：私钥留在 HSM 里，签名由云服务完成，本地只拿签名结果。
- **SignPath.io / SignPath Foundation**：托管签名服务，开源项目可免费；私钥由服务保管。
- **发布到 Microsoft Store**：MSIX/Store 发布由微软签名（MSI 不适用）。

共同点：签名都需要**一张受信任的代码签名证书**（或能访问这样一个服务的账号），"用哪个工具"只是操作方式不同。

## 自签名证书：什么时候够用

没有买证书也能签，本仓库有两个脚本：

```powershell
# 1) 生成一张自签名代码签名证书（不需要管理员），导出 pfx（含私钥，用来签）和 cer（公开部分，用来信任）
pwsh -File build/signing/windows/New-SigningCertificate.ps1 -Years 5
#    → build/signing/windows/certificates/RemoteDesktop.pfx  .cer

# 2) 用签名构建（证书已在当前用户存储里，脚本会自动选用）
pwsh -File build/installer/client/windows/build-installer.ps1 -RendezvousUri ws://<服务器>:5080/ws

# 3) 在**需要信任这些构建的机器**上装公开部分（管理员；-Scope CurrentUser 则只影响当前用户）
#    用 MSI 安装时这一步是自动的，见下面的说明；手工装/直接跑程序时才需要
pwsh -File build/signing/windows/Install-TrustedCertificate.ps1 -CertificatePath build/signing/windows/certificates/RemoteDesktop.cer

# 撤销：先取消信任，再从证书存储里删掉
pwsh -File build/signing/windows/Install-TrustedCertificate.ps1 -CertificatePath ...\RemoteDesktop.cer -Remove
pwsh -File build/signing/windows/New-SigningCertificate.ps1 -RemoveFromStore
```

**第 2 步不用加额外参数**：验证把两件事分开看——

- **签名本身是否完整**（文件有没有被改过、签名有没有写上）：不通过就构建失败，这是发布必须保证的；
- **证书是否被本机信任**：自签名证书在装过 cer 之前一定"链到不受信任的根"。这只是**警告**，并打印出上面第 3 步的命令。构建不会因此失败。

（`-SkipSignatureVerification` 仍然可用，含义是"这一整步都别做"，一般不需要。）

### 用安装包装的机器不用手工装 cer

构建时如果签名用的是**自签名**证书，脚本会把它的公开部分（`.cer`）一起放进 MSI：安装时由安装程序把它写进
**本机的"受信任的根证书颁发机构"**，卸载时移除。所以上面第 3 步只对"手工拷贝程序去跑"的机器还有意义。

这一步是给 UIAccess 用的：会话代理要以 UIAccess 权限启动，Windows 要求它的签名能连到一条**本机信任**的证书链，
而自签名证书在没被信任之前连不上。安装包自己把根装好，就不必让人先手工 `certutil -addstore` 再安装。

CA 签发的证书**不会**被这样做：它的链本来就受信任，把一张终端证书塞进根存储反而是降级——那样一来这台机器信任的根
就是这把签名密钥，而不是那家 CA，CA 轮换或吊销都影响不到它。

（包里的证书来自构建时用的那张，签发者为自己的证书才带；`InstallerPackageTests` 会检查这三件事同时出现或同时
不出现：证书行、它的二进制内容、以及负责卸载时清掉它的注册表键路径。）

### 同一套机制，给信令服务器的证书用

`-ServerCertificate <证书文件>` 会把**信令服务器用的那张证书**的公钥部分一起打进安装包，装到哪台机器，
哪台机器就信任它。这是给"服务器地址没有公网 CA 会签发证书"的部署准备的（内网、车间、没有域名的机器）：
配上 TLS 之后客户端不会再报证书错误。`build-installer.ps1` 把它作为 `-p:TrustedServerCertificate` 传给 WiX，
`Components.wxs` 里对应的是第二个条件组件，和上面那一个互不影响。

生成服务器证书用 `New-ServerCertificate.ps1`（列上客户端会用的所有名字和地址）：

```powershell
# rd.lan.pfx 给服务器（含私钥），rd.lan.cer 给客户端
.\build\signing\New-ServerCertificate.ps1 -DnsName rd.lan -IpAddress 192.168.1.10
```

完整的部署步骤见 [docs/deployment.md](../../docs/deployment.md) 的"生产环境：信令必须走 TLS"。
同样是那句话：把自签证书装进受信任根，等于持有它私钥的人能在这些机器上冒充任何服务器，私钥不要外传。

**它买到什么、买不到什么**

| | 自签名 | CA 签发（OV/EV）或云签名 |
|---|---|---|
| 文件带签名、能校验内容是否被改过 | ✅ | ✅ |
| 在**你装过 cer 的机器**上显示为有效、不再问"是否信任此发布者" | ✅ | ✅ |
| 在用户自己的机器上消除 SmartScreen「未知发布者」 | ❌ 仍然提示 | ✅ |
| 企业内部策略（AppLocker / WDAC 只允许已签名程序） | ✅ 这正是自签名的正当用法 | ✅ |
| 成本 | 0 | OV 约 ¥1000–3000/年；EV 更贵；Azure Trusted Signing 约 $10/月 |

注意：自签名**不比不签名更"安全"**——在没装 cer 的机器上，两者都显示为未知发布者；而且自签名的主体名可以随便填，所以它不能证明"这是谁做的软件"，只能证明"这份文件和拿着私钥的那台机器签的那份一致"。私钥文件（`certificates/` 目录已加入 .gitignore）就是这把钥匙本身，泄露等于别人能以你的名义签名。

**要对外发布**就别停留在自签名：买一张 OV/EV 证书，或者用 Azure Trusted Signing（不用自己保管证书，按月计费）、SignPath（开源可免费）。

## 命令速查

```powershell
# 自动找证书并签名（默认行为）
pwsh -File build/installer/client/windows/build-installer.ps1 -RendezvousUri ws://<服务器>:5080/ws

# 指定证书（指纹或 CN 名称的一部分）
pwsh -File build/installer/client/windows/build-installer.ps1 -SignCertificate 1A2B3C... 
pwsh -File build/installer/client/windows/build-installer.ps1 -SignCertificate "CN=你的公司名"

# 明确不签名
pwsh -File build/installer/client/windows/build-installer.ps1 -SkipSigning

# 跳过签名验证（一般不需要：自签名证书只是"不受信任的根"警告，不会让构建失败）
pwsh -File build/installer/client/windows/build-installer.ps1 -SkipSignatureVerification

# 换时间戳服务器（默认 DigiCert；留空则不时间戳，正式发布不建议）
pwsh -File build/installer/client/windows/build-installer.ps1 -TimestampServer http://timestamp.sectigo.com
```

**时间戳为什么重要**：不时间戳的签名在证书过期后失效，已经发出去的安装包会突然"签名无效"；有时间戳的签名在证书过期后依然有效。

## 检查签名

```powershell
# 安装包
Get-AuthenticodeSignature .\build\installer\client\windows\artifacts\RemoteDesktop-0.2.0.0-x64.msi | Format-List Status, SignerCertificate

# 装好的程序
Get-AuthenticodeSignature 'C:\Program Files\RemoteDesktop\RemoteDesktop.Client.exe' | Format-List Status, SignerCertificate

# 用 signtool（装了 SDK 时）
signtool verify /pa /v .\artifacts\RemoteDesktop-0.2.0.0-x64.msi
```

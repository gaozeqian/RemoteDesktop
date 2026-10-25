//
//  rd_installer_plugin.m
//
//  The macOS Installer pane that asks the questions Linux asks through debconf: which database the server keeps
//  device codes in, whether signaling uses TLS and where the certificate comes from, and which signaling address the
//  client and the host should use.
//
//  Why an Installer plugin rather than the old build-time flags: a .pkg has no MSI property mechanism, so the only
//  way to collect these answers at install time is the Installer's own pane API. The pane writes an XML plist to
//  /var/tmp and the package's preinstall moves it out of the user-writable directory; the postinstall reads it as
//  root and deletes it. The same plist shape is accepted at
//  /Library/Application Support/RemoteDesktop/install-answers.plist for unattended installs, so `installer -pkg`
//  without a GUI still has a supported path.
//
//  Build (see native/macos/build-installer-plugin.sh):
//    clang -fobjc-arc -bundle -framework Cocoa -framework InstallerPlugins ...
//
//  The role (server / client / host) is injected into the bundle's Info.plist as RDRole; one source builds three
//  slightly different panes.
//

#import <Cocoa/Cocoa.h>
#import <InstallerPlugins/InstallerSection.h>
#import <InstallerPlugins/InstallerPane.h>

static NSString *const RDInstallDirectory = @"/usr/local/remotedesktop-server";
static NSString *const RDSummaryPath = @"/usr/local/remotedesktop-server/install-summary.plist";
static NSString *const RDMachineDirectory = @"/Library/Application Support/RemoteDesktop";
static NSString *const RDMachineSettingsPath = @"/Library/Application Support/RemoteDesktop/appsettings.json";
static NSString *const RDDefaultRendezvous = @"ws://127.0.0.1:5080/ws";
static NSString *const RDDefaultDatabasePath = @"/usr/local/remotedesktop-server/var/rendezvous.db";
// The field is 272 pt wide inside the Installer's fixed content area, so the placeholder has to be a complete
// connection string that fits rather than the full example: the full one is ~450 pt and was truncated mid-word.
static NSString *const RDPostgresPlaceholder = @"Host=db;Database=rd;Username=rd";
static NSString *const RDPostgresExample =
    @"完整示例：Host=db;Port=5432;Database=remotedesktop;Username=rd;Password=…";
static NSString *const RDRoleKey = @"RDRole";

static NSString *RDAnswersPath(NSString *role)
{
    return [NSString stringWithFormat:@"/var/tmp/com.remotedesktop.install.%@.plist", role];
}

static NSString *RDRoleForBundle(NSBundle *bundle)
{
    NSString *role = bundle.infoDictionary[RDRoleKey];
    return [role isKindOfClass:[NSString class]] && role.length > 0 ? role : @"server";
}

static void RDAlert(NSString *text)
{
    NSAlert *alert = [[NSAlert alloc] init];
    alert.messageText = @"无法继续安装";
    alert.informativeText = text;
    [alert addButtonWithTitle:@"好"];
    [alert runModal];
}

static NSTextField *RDLabel(NSView *parent, NSString *text, NSRect frame, BOOL bold)
{
    NSTextField *label = [NSTextField labelWithString:text];
    label.frame = frame;
    label.font = bold ? [NSFont boldSystemFontOfSize:15] : [NSFont systemFontOfSize:12];
    label.textColor = bold ? [NSColor labelColor] : [NSColor secondaryLabelColor];
    [parent addSubview:label];
    return label;
}

static NSTextField *RDField(NSView *parent, NSRect frame, NSString *placeholder)
{
    NSTextField *field = [[NSTextField alloc] initWithFrame:frame];
    field.bezeled = YES;
    field.drawsBackground = YES;
    field.editable = YES;
    field.selectable = YES;
    field.font = [NSFont systemFontOfSize:13];
    field.placeholderString = placeholder ?: @"";
    [parent addSubview:field];
    return field;
}

static NSSecureTextField *RDSecureField(NSView *parent, NSRect frame, NSString *placeholder)
{
    NSSecureTextField *field = [[NSSecureTextField alloc] initWithFrame:frame];
    field.bezeled = YES;
    field.drawsBackground = YES;
    field.editable = YES;
    field.selectable = YES;
    field.font = [NSFont systemFontOfSize:13];
    field.placeholderString = placeholder ?: @"";
    [parent addSubview:field];
    return field;
}

static NSPopUpButton *RDPopup(NSView *parent, NSRect frame, NSArray<NSString *> *items, id target, SEL action)
{
    NSPopUpButton *popup = [[NSPopUpButton alloc] initWithFrame:frame pullsDown:NO];
    [popup addItemsWithTitles:items];

    // Do not let AppKit validate these items through the popup's target. InstallerPane is an NSMenuValidation
    // participant for the Installer's own commands and answers NO to actions it does not know, so an auto-enabled
    // popup opened with every choice greyed out and the pane could not be configured. The control's own enabled
    // state (set in updateEnabledState) is the only thing that should decide whether a choice can be picked.
    popup.menu.autoenablesItems = NO;
    for (NSMenuItem *item in popup.itemArray)
    {
        item.enabled = YES;
    }
    popup.target = target;
    popup.action = action;
    [parent addSubview:popup];
    return popup;
}

static NSButton *RDButton(NSView *parent, NSRect frame, NSString *title, id target, SEL action, NSInteger tag)
{
    NSButton *button = [NSButton buttonWithTitle:title target:target action:action];
    button.frame = frame;
    button.bezelStyle = NSBezelStyleRounded;
    button.tag = tag;
    [parent addSubview:button];
    return button;
}

static NSTextField *RDNote(NSView *parent, NSRect frame)
{
    NSTextField *note = [NSTextField wrappingLabelWithString:@""];
    note.frame = frame;
    // 10.5 rather than 11: the TLS "generate" note carries two full paths and needs three lines at 11 pt, which does
    // not fit the fixed 421x329 pane. At 10.5 it wraps to two lines and stays inside its frame.
    note.font = [NSFont systemFontOfSize:10.5];
    note.textColor = [NSColor secondaryLabelColor];
    [parent addSubview:note];
    return note;
}

#pragma mark - Section

@class RDInstallerPane;

@interface RDInstallerSection : InstallerSection
// The Installer keeps the content view but, on macOS 26, releases the pane returned by firstPane immediately after
// taking that view. NSControl.target and NSTextField.delegate are weak, so every popup/checkbox action and every
// field-change callback would go to a deallocated pane: the menus appeared, and picking an item only changed the
// title because no action arrived. The section owns the pane for as long as the section is in use.
@property (nonatomic, strong) RDInstallerPane *pane;
@end

#pragma mark - Pane

@interface RDInstallerPane : InstallerPane <NSTextFieldDelegate>
@end

@interface RDInstallerPane ()
- (void)buildView;
- (void)loadExistingState;
- (void)updateEnabledState;
- (BOOL)isServerRole;
- (BOOL)writeServerAnswers;
- (BOOL)writeClientAnswers;
- (BOOL)writeAnswers:(NSDictionary *)answers;
@end

@implementation RDInstallerSection

// No nib: the pane builds its view in code. The Installer calls this when the section becomes the active one.
- (InstallerPane *)firstPane
{
    if (self.pane == nil)
    {
        self.pane = [[RDInstallerPane alloc] initWithSection:self];
    }
    return self.pane;
}

@end

@implementation RDInstallerPane
{
    NSView *_contentView;
    NSString *_role;
    NSDictionary *_summary;
    BOOL _existingInstall;

    NSButton *_preserveCheckbox;
    NSTextField *_preserveNote;

    NSPopUpButton *_databasePopup;
    NSTextField *_databasePathField;
    NSTextField *_postgresField;
    NSTextField *_databaseNote;

    NSPopUpButton *_tlsPopup;
    NSTextField *_certificateField;
    NSButton *_chooseCertificateButton;
    NSSecureTextField *_certificatePasswordField;
    NSTextField *_hostnameField;
    NSTextField *_tlsNote;

    NSTextField *_addressField;
    NSTextField *_serverCertificateField;
    NSButton *_chooseServerCertificateButton;
    NSSecureTextField *_fixedPasswordField;
    NSSecureTextField *_fixedPasswordConfirmField;
    NSTextField *_addressNote;
}

- (instancetype)initWithSection:(id)parent
{
    self = [super initWithSection:parent];
    if (self)
    {
        _role = RDRoleForBundle([NSBundle bundleForClass:[RDInstallerSection class]]);
        // A cancelled or failed earlier attempt must not leave a secret-bearing answer file behind for the next
        // postinstall to pick up. It is written again only when the person clicks Continue.
        [[NSFileManager defaultManager] removeItemAtPath:RDAnswersPath(_role) error:nil];
        [self buildView];
        [self loadExistingState];
        [self updateEnabledState];
    }
    return self;
}

- (void)dealloc
{
    // The pane's answers can now contain the unattended password. If the person cancels after Continue, the file in
    // /var/tmp would otherwise outlive the installer; preinstall removes it on a real installation, and this covers
    // the cancel path. A successful install has already moved it to a root-only copy, so removing it here is a no-op.
    [[NSFileManager defaultManager] removeItemAtPath:RDAnswersPath(_role) error:nil];
}

- (NSString *)title
{
    if ([_role isEqualToString:@"server"])
    {
        return @"服务器配置";
    }
    return [NSString stringWithFormat:@"%@配置", [_role isEqualToString:@"host"] ? @"被控端" : @"客户端"];
}

- (NSView *)contentView
{
    return _contentView;
}

- (NSView *)initialKeyView
{
    return [self isServerRole] ? _databasePopup : _addressField;
}

- (BOOL)isServerRole
{
    return [_role isEqualToString:@"server"];
}

- (void)buildView
{
    // A custom pane gets a 421x329 pt content area from the Installer (measured on macOS 26); anything beyond that is
    // clipped, not scrolled. This view therefore fits that area — the old 640x430 view lost its heading, the right
    // end of every field and the bottom notes. The Installer draws the section title ("服务器配置"/"客户端配置"), so
    // this view does not repeat it.
    const CGFloat width = 410.0;
    const CGFloat labelWidth = 112.0;
    const CGFloat margin = 8.0;
    const CGFloat fieldX = margin + labelWidth + 10.0;
    const CGFloat fieldWidth = width - fieldX - margin;
    const CGFloat labelHeight = 17.0;

    _contentView = [[NSView alloc] initWithFrame:NSMakeRect(0.0, 0.0, width, 320.0)];
    _contentView.autoresizingMask = NSViewWidthSizable;

    if ([self isServerRole])
    {
        _preserveCheckbox = [NSButton checkboxWithTitle:@"保留现有设置（升级时推荐）"
                                                 target:self
                                                 action:@selector(preserveChanged:)];
        _preserveCheckbox.frame = NSMakeRect(margin, 290.0, width - 2 * margin, 20.0);
        _preserveCheckbox.hidden = YES;
        [_contentView addSubview:_preserveCheckbox];

        _preserveNote = RDNote(_contentView, NSMakeRect(margin, 256.0, width - 2 * margin, 30.0));

        RDLabel(_contentView, @"数据库", NSMakeRect(margin, 234.0, labelWidth, labelHeight), NO);
        _databasePopup = RDPopup(_contentView, NSMakeRect(fieldX, 229.0, fieldWidth, 26.0),
                                 @[@"SQLite（本机文件）", @"PostgreSQL"], self, @selector(databaseChanged:));

        RDLabel(_contentView, @"SQLite 文件", NSMakeRect(margin, 205.0, labelWidth, labelHeight), NO);
        _databasePathField = RDField(_contentView, NSMakeRect(fieldX, 200.0, fieldWidth, 24.0),
                                     RDDefaultDatabasePath);

        RDLabel(_contentView, @"PostgreSQL 连接串", NSMakeRect(margin, 176.0, labelWidth, labelHeight), NO);
        _postgresField = RDField(_contentView, NSMakeRect(fieldX, 171.0, fieldWidth, 24.0),
                                 RDPostgresPlaceholder);
        _postgresField.toolTip = RDPostgresExample;

        RDLabel(_contentView, @"TLS", NSMakeRect(margin, 147.0, labelWidth, labelHeight), NO);
        _tlsPopup = RDPopup(_contentView, NSMakeRect(fieldX, 142.0, fieldWidth, 26.0),
                            @[@"不使用 TLS（http）", @"生成自签名证书", @"使用已有证书"],
                            self, @selector(tlsChanged:));

        RDLabel(_contentView, @"证书文件", NSMakeRect(margin, 118.0, labelWidth, labelHeight), NO);
        _certificateField = RDField(_contentView, NSMakeRect(fieldX, 113.0, fieldWidth - 84.0, 24.0),
                                    @"/path/to/signaling.pfx 或 signaling.crt");
        _chooseCertificateButton = RDButton(_contentView, NSMakeRect(width - margin - 76.0, 111.0, 76.0, 28.0),
                                            @"选择…", self, @selector(chooseCertificate:), 1);

        RDLabel(_contentView, @"证书口令", NSMakeRect(margin, 89.0, labelWidth, labelHeight), NO);
        _certificatePasswordField = RDSecureField(_contentView, NSMakeRect(fieldX, 84.0, fieldWidth, 24.0),
                                                  @"PKCS#12 口令；PEM 留空");

        RDLabel(_contentView, @"证书域名/IP", NSMakeRect(margin, 60.0, labelWidth, labelHeight), NO);
        _hostnameField = RDField(_contentView, NSMakeRect(fieldX, 55.0, fieldWidth, 24.0),
                                 @"自签名证书要覆盖的域名或 IP，逗号分隔；留空用本机名");

        _databaseNote = RDNote(_contentView, NSMakeRect(margin, 32.0, width - 2 * margin, 28.0));
        _tlsNote = RDNote(_contentView, NSMakeRect(margin, 2.0, width - 2 * margin, 28.0));
    }
    else
    {
        RDNote(_contentView, NSMakeRect(margin, 284.0, width - 2 * margin, 30.0)).stringValue =
            [_role isEqualToString:@"host"]
                ? @"填写信令服务器地址和无人值守密码；证书只在 wss:// 自签名时需要。安装后可用命令行或重新安装修改。"
                : @"填写信令服务器地址；只有自签名证书才需要提供证书。安装后可在应用里修改。";

        RDLabel(_contentView, @"服务器地址", NSMakeRect(margin, 248.0, labelWidth, labelHeight), NO);
        _addressField = RDField(_contentView, NSMakeRect(fieldX, 237.0, fieldWidth, 24.0),
                                @"wss://rd.example.com:5080/ws");
        _addressField.delegate = self;

        RDLabel(_contentView, @"服务器证书", NSMakeRect(margin, 204.0, labelWidth, labelHeight), NO);
        _serverCertificateField = RDField(_contentView, NSMakeRect(fieldX, 193.0, fieldWidth - 84.0, 24.0),
                                          @"自签名证书（PEM/DER）；公开可信证书留空");
        _chooseServerCertificateButton = RDButton(_contentView,
                                                  NSMakeRect(width - margin - 76.0, 191.0, 76.0, 28.0),
                                                  @"选择…", self, @selector(chooseCertificate:), 2);

        NSString *note = @"地址必须是 ws:// 或 wss:// 开头的绝对地址。ws:// 不需要证书；wss:// 使用公开可信证书时也不需要。"
            @"只有服务器用自签名证书时才在这里选择它的公钥；安装器会把它固定为唯一受信任的服务器证书，不修改系统信任设置。";

        if ([_role isEqualToString:@"host"])
        {
            // A host runs without a window, so the installer is the only place a person can type the password that
            // makes unattended access work. Blank means "keep whatever is already there" — the pane never shows the
            // stored password, so an upgrade must not clear it just because the field is empty.
            RDLabel(_contentView, @"无人值守密码", NSMakeRect(margin, 160.0, labelWidth, labelHeight), NO);
            _fixedPasswordField = RDSecureField(_contentView, NSMakeRect(fieldX, 149.0, fieldWidth, 24.0),
                                                @"至少 8 位；留空则不修改/不设置");
            RDLabel(_contentView, @"确认密码", NSMakeRect(margin, 126.0, labelWidth, labelHeight), NO);
            _fixedPasswordConfirmField = RDSecureField(_contentView, NSMakeRect(fieldX, 115.0, fieldWidth, 24.0),
                                                       @"再次输入");

            note = [note stringByAppendingString:
                @"无人值守密码至少 8 位，保存在当前登录用户的钥匙串里；留空则保持现有密码不变。"];
            _addressNote = RDNote(_contentView, NSMakeRect(margin, 18.0, width - 2 * margin, 88.0));
        }
        else
        {
            _addressNote = RDNote(_contentView, NSMakeRect(margin, 90.0, width - 2 * margin, 90.0));
        }
        _addressNote.stringValue = note;
    }
}

- (void)loadExistingState
{
    if ([self isServerRole])
    {
        _existingInstall = [[NSFileManager defaultManager]
            fileExistsAtPath:[RDInstallDirectory stringByAppendingPathComponent:@"Rendezvous/appsettings.json"]];

        if (!_existingInstall)
        {
            _databasePathField.stringValue = RDDefaultDatabasePath;
            _preserveCheckbox.hidden = YES;
            _preserveCheckbox.state = NSControlStateValueOff;
            return;
        }

        _preserveCheckbox.hidden = NO;
        _preserveCheckbox.state = NSControlStateValueOn;
        _summary = [NSDictionary dictionaryWithContentsOfFile:RDSummaryPath];

        NSString *provider = [_summary[@"DatabaseProvider"] isKindOfClass:[NSString class]]
            ? _summary[@"DatabaseProvider"] : @"sqlite";
        [_databasePopup selectItemAtIndex:[provider isEqualToString:@"postgres"] ? 1 : 0];

        NSString *path = [_summary[@"DatabasePath"] isKindOfClass:[NSString class]]
            ? _summary[@"DatabasePath"] : RDDefaultDatabasePath;
        _databasePathField.stringValue = path;
        _postgresField.placeholderString = [_summary[@"HasPostgresConnectionString"] boolValue]
            ? @"留空保留现有连接串"
            : RDPostgresPlaceholder;

        NSString *tls = [_summary[@"TlsMode"] isKindOfClass:[NSString class]]
            ? _summary[@"TlsMode"] : @"none";
        NSInteger tlsIndex = [tls isEqualToString:@"generate"] ? 1 : ([tls isEqualToString:@"existing"] ? 2 : 0);
        [_tlsPopup selectItemAtIndex:tlsIndex];

        NSString *certificate = [_summary[@"TlsCertificatePath"] isKindOfClass:[NSString class]]
            ? _summary[@"TlsCertificatePath"] : @"";
        _certificateField.stringValue = certificate;

        _preserveNote.stringValue = _summary
            ? @"已检测到现有安装。默认保留当前数据库与 TLS 设置；取消勾选可以重新配置。"
            : @"已检测到现有安装，但没有找到上次安装的摘要；默认保留当前设置。取消勾选可以重新配置。";
        return;
    }

    NSString *address = RDDefaultRendezvous;
    NSData *data = [NSData dataWithContentsOfFile:RDMachineSettingsPath];
    if (data)
    {
        id root = [NSJSONSerialization JSONObjectWithData:data options:0 error:NULL];
        if ([root isKindOfClass:[NSDictionary class]])
        {
            id transport = root[@"Transport"];
            if ([transport isKindOfClass:[NSDictionary class]])
            {
                id value = transport[@"RendezvousUri"];
                if ([value isKindOfClass:[NSString class]] && [value length] > 0)
                {
                    address = value;
                }
            }
        }
    }
    _addressField.stringValue = address;
}

- (void)updateEnabledState
{
    if ([self isServerRole])
    {
        BOOL preserve = !_preserveCheckbox.hidden && _preserveCheckbox.state == NSControlStateValueOn;
        BOOL editable = !preserve;
        BOOL sqlite = _databasePopup.indexOfSelectedItem == 0;
        NSInteger tls = _tlsPopup.indexOfSelectedItem;

        // One note serves all three states: fresh installation, upgrade preserving, upgrade reconfiguring. Keeping
        // it visible instead of hiding it on a fresh install is also what tells the person why the controls below
        // are greyed out when "保留现有设置" is ticked.
        _preserveNote.hidden = NO;
        if (!_existingInstall)
        {
            _preserveNote.stringValue =
                @"选择设备代码的存储位置与信令的 TLS 方式；安装后可用 configure-server.sh 修改。";
        }
        else if (preserve)
        {
            _preserveNote.stringValue = _summary
                ? @"已检测到现有安装。当前数据库与 TLS 设置将保留；取消勾选可以重新配置。"
                : @"已检测到现有安装，但没有上次安装的摘要；当前设置将保留。取消勾选可以重新配置。";
        }
        else
        {
            _preserveNote.stringValue = @"已取消保留：下面的选择会覆盖现有的数据库与 TLS 设置。";
        }

        [_databasePopup setEnabled:editable];
        [_databasePathField setEnabled:editable && sqlite];
        [_postgresField setEnabled:editable && !sqlite];
        [_tlsPopup setEnabled:editable];
        [_certificateField setEnabled:editable && tls == 2];
        [_chooseCertificateButton setEnabled:editable && tls == 2];
        [_certificatePasswordField setEnabled:editable && tls == 2];
        [_hostnameField setEnabled:editable && tls == 1];

        if (!editable)
        {
            _databaseNote.stringValue = @"";
            _tlsNote.stringValue = @"";
        }
        else
        {
            if (sqlite)
            {
                _databaseNote.stringValue =
                    @"设备代码保存在本机 SQLite 文件中，重启与升级都不变。这个文件要备份：删掉它等于所有设备码重发。"
                    @"监控台也使用同一目录下的 ops.db。";
            }
            else
            {
                _databaseNote.stringValue =
                    @"设备代码保存在 PostgreSQL 中。连接串会写进 root 只读的 appsettings.json；监控台使用同一个数据库。";
            }

            switch (tls)
            {
                case 1:
                    _tlsNote.stringValue =
                        @"生成在安装目录 tls/；公钥导出到 /usr/local/share/doc/remotedesktop-server/signaling.crt，"
                        @"客户端和 host 选择它。";
                    break;
                case 2:
                    _tlsNote.stringValue =
                        @"证书口令只用于本次安装，不会写进摘要；证书路径不变时留空即可沿用现有口令，换了证书才需要重新输入。";
                    break;
                default:
                    _tlsNote.stringValue =
                        @"信令走明文 http：设备码、谁在连谁以及中继票据都能被同一网络上的其他人读到。";
                    break;
            }
        }
        return;
    }

    NSString *address = [_addressField.stringValue stringByTrimmingCharactersInSet:
        [NSCharacterSet whitespaceAndNewlineCharacterSet]];
    BOOL wss = [[address lowercaseString] hasPrefix:@"wss://"];
    [_serverCertificateField setEnabled:wss];
    [_chooseServerCertificateButton setEnabled:wss];
    if (!wss)
    {
        _serverCertificateField.stringValue = @"";
    }
}

- (void)preserveChanged:(id)sender
{
    [self updateEnabledState];
}

- (void)databaseChanged:(id)sender
{
    [self updateEnabledState];
}

- (void)tlsChanged:(id)sender
{
    [self updateEnabledState];
}

- (void)controlTextDidChange:(NSNotification *)notification
{
    if (notification.object == _addressField)
    {
        [self updateEnabledState];
    }
}

- (void)chooseCertificate:(id)sender
{
    NSOpenPanel *panel = [NSOpenPanel openPanel];
    panel.canChooseFiles = YES;
    panel.canChooseDirectories = NO;
    panel.allowsMultipleSelection = NO;
    panel.message = ((NSButton *)sender).tag == 1
        ? @"选择服务器的 TLS 证书（.pfx/.p12，或带同名 .key 的 PEM）"
        : @"选择服务器证书的公钥（PEM 或 DER）";

    if ([panel runModal] != NSModalResponseOK)
    {
        return;
    }

    if (((NSButton *)sender).tag == 1)
    {
        _certificateField.stringValue = panel.URL.path;
    }
    else
    {
        _serverCertificateField.stringValue = panel.URL.path;
    }
}

- (BOOL)shouldExitPane:(InstallerSectionDirection)dir
{
    if ([self isServerRole])
    {
        return [self writeServerAnswers];
    }
    return [self writeClientAnswers];
}

- (BOOL)writeServerAnswers
{
    BOOL preserve = _existingInstall && _preserveCheckbox.state == NSControlStateValueOn;
    if (preserve)
    {
        return [self writeAnswers:@{@"Role": _role, @"PreserveExisting": @YES}];
    }

    BOOL sqlite = _databasePopup.indexOfSelectedItem == 0;
    NSString *databasePath = [_databasePathField.stringValue stringByTrimmingCharactersInSet:
        [NSCharacterSet whitespaceAndNewlineCharacterSet]];
    NSString *postgres = [_postgresField.stringValue stringByTrimmingCharactersInSet:
        [NSCharacterSet whitespaceAndNewlineCharacterSet]];

    if (sqlite)
    {
        if (databasePath.length > 0 && !databasePath.isAbsolutePath)
        {
            RDAlert(@"SQLite 文件路径必须是绝对路径。");
            return NO;
        }
    }
    else
    {
        BOOL hasExisting = [_summary[@"HasPostgresConnectionString"] boolValue];
        if (postgres.length == 0 && !hasExisting)
        {
            RDAlert(@"选择了 PostgreSQL，但没有填写连接串。请填写连接串，或改回 SQLite。");
            return NO;
        }
    }

    NSInteger tls = _tlsPopup.indexOfSelectedItem;
    NSString *certificate = [_certificateField.stringValue stringByTrimmingCharactersInSet:
        [NSCharacterSet whitespaceAndNewlineCharacterSet]];
    NSString *hostname = [_hostnameField.stringValue stringByTrimmingCharactersInSet:
        [NSCharacterSet whitespaceAndNewlineCharacterSet]];

    if (tls == 2)
    {
        if (certificate.length == 0)
        {
            RDAlert(@"选择了“使用已有证书”，但没有选择证书文件。");
            return NO;
        }
        if (!certificate.isAbsolutePath)
        {
            RDAlert(@"证书文件路径必须是绝对路径。");
            return NO;
        }
        if (![[NSFileManager defaultManager] fileExistsAtPath:certificate])
        {
            RDAlert([NSString stringWithFormat:@"证书文件不存在：%@", certificate]);
            return NO;
        }
    }

    NSMutableDictionary *answers = [NSMutableDictionary dictionary];
    answers[@"Role"] = _role;
    answers[@"PreserveExisting"] = @NO;
    answers[@"DatabaseProvider"] = sqlite ? @"sqlite" : @"postgres";
    if (sqlite)
    {
        if (databasePath.length > 0)
        {
            answers[@"DatabasePath"] = databasePath;
        }
    }
    else
    {
        if (postgres.length > 0)
        {
            answers[@"PostgresConnectionString"] = postgres;
        }
    }

    answers[@"TlsMode"] = @[@"none", @"generate", @"existing"][tls];
    if (tls == 1)
    {
        if (hostname.length > 0)
        {
            answers[@"TlsHostName"] = hostname;
        }
    }
    else if (tls == 2)
    {
        answers[@"TlsCertificatePath"] = certificate;
        NSString *password = _certificatePasswordField.stringValue;
        if (password.length > 0)
        {
            answers[@"TlsPassword"] = password;
        }
    }

    return [self writeAnswers:answers];
}

- (BOOL)writeClientAnswers
{
    NSString *address = [_addressField.stringValue stringByTrimmingCharactersInSet:
        [NSCharacterSet whitespaceAndNewlineCharacterSet]];
    if (address.length == 0)
    {
        RDAlert(@"请填写服务器地址，例如 wss://rd.example.com:5080/ws。");
        return NO;
    }

    NSURLComponents *components = [NSURLComponents componentsWithString:address];
    NSString *scheme = [components.scheme lowercaseString];
    if (!components || components.host.length == 0
        || !([scheme isEqualToString:@"ws"] || [scheme isEqualToString:@"wss"]))
    {
        RDAlert(@"服务器地址必须是 ws:// 或 wss:// 开头的绝对地址，例如 wss://rd.example.com:5080/ws。");
        return NO;
    }

    NSString *certificate = [_serverCertificateField.stringValue stringByTrimmingCharactersInSet:
        [NSCharacterSet whitespaceAndNewlineCharacterSet]];
    if ([scheme isEqualToString:@"ws"] && certificate.length > 0)
    {
        RDAlert(@"服务器地址是 ws://，不需要证书。请清空证书，或把地址改成 wss://。");
        return NO;
    }
    if (certificate.length > 0 && (!certificate.isAbsolutePath
        || ![[NSFileManager defaultManager] fileExistsAtPath:certificate]))
    {
        RDAlert([NSString stringWithFormat:@"证书文件不存在或不是绝对路径：%@", certificate]);
        return NO;
    }

    NSString *unattendedPassword = @"";
    if ([_role isEqualToString:@"host"])
    {
        unattendedPassword = [_fixedPasswordField.stringValue stringByTrimmingCharactersInSet:
            [NSCharacterSet whitespaceAndNewlineCharacterSet]];
        NSString *confirmation = [_fixedPasswordConfirmField.stringValue stringByTrimmingCharactersInSet:
            [NSCharacterSet whitespaceAndNewlineCharacterSet]];

        if (unattendedPassword.length > 0)
        {
            if (unattendedPassword.length < 8)
            {
                RDAlert(@"无人值守密码至少需要 8 个字符。");
                return NO;
            }
            if (![unattendedPassword isEqualToString:confirmation])
            {
                RDAlert(@"两次输入的无人值守密码不一致。");
                return NO;
            }
        }
        else if (confirmation.length > 0)
        {
            RDAlert(@"请在上面的“无人值守密码”里输入密码，或清空两栏以保持现有密码不变。");
            return NO;
        }
    }

    NSMutableDictionary *answers = [NSMutableDictionary dictionary];
    answers[@"Role"] = _role;
    answers[@"RendezvousUri"] = address;
    if (certificate.length > 0)
    {
        answers[@"ServerCertificatePath"] = certificate;
    }
    if (unattendedPassword.length > 0)
    {
        answers[@"UnattendedPassword"] = unattendedPassword;
    }
    return [self writeAnswers:answers];
}

- (BOOL)writeAnswers:(NSDictionary *)answers
{
    NSString *path = RDAnswersPath(_role);
    NSData *data = [NSPropertyListSerialization dataWithPropertyList:answers
                                                              format:NSPropertyListXMLFormat_v1_0
                                                             options:0
                                                               error:NULL];
    if (!data)
    {
        RDAlert([NSString stringWithFormat:@"无法写入安装答案：%@", path]);
        return NO;
    }

    // The file can carry a PostgreSQL connection string and a certificate password. It exists only from the moment
    // Continue is clicked until the postinstall has applied it; the preinstall moves it to a root-owned copy and the
    // postinstall deletes that copy. Created with 0600 in the same call rather than written and then chmodded: the
    // short window between those two would leave the secrets readable under the user's umask.
    [[NSFileManager defaultManager] removeItemAtPath:path error:NULL];
    if (![[NSFileManager defaultManager] createFileAtPath:path
                                                 contents:data
                                               attributes:@{NSFilePosixPermissions: @(0600)}])
    {
        RDAlert([NSString stringWithFormat:@"无法写入安装答案：%@", path]);
        return NO;
    }
    return YES;
}

@end

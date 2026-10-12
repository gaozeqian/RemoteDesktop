//
//  rd_system.m
//
//  Opening the Privacy & Security pane a permission is granted in.
//
//  This is here rather than in managed code because the alternative is spawning /usr/bin/open, and a remote-desktop
//  host that shells out to launch things is a habit worth not forming. NSWorkspace is the API for it, it is
//  Objective-C, and the Objective-C already lives in this library.
//
//  The URL scheme is not a private API but it is also not formally documented: `x-apple.systempreferences` with a
//  `com.apple.preference.security` host and a `Privacy_*` anchor is what every macOS app that sends users to
//  System Settings uses, and it has survived the System Settings rewrite in macOS 13. The anchors the platform
//  layer needs are named in TccPermission's managed side.
//

#import "rd_native.h"

#import <AppKit/AppKit.h>

RD_EXPORT int rd_open_settings(const char *anchor)
{
    if (anchor == NULL || anchor[0] == '\0') {
        return RD_ERROR_ARGUMENT;
    }

    @autoreleasepool {
        NSString *suffix = [NSString stringWithUTF8String:anchor];
        if (suffix == nil) {
            return RD_ERROR_ARGUMENT;
        }

        NSString *text = [@"x-apple.systempreferences:com.apple.preference.security?" stringByAppendingString:suffix];
        NSURL *url = [NSURL URLWithString:text];
        if (url == nil) {
            return RD_ERROR_ARGUMENT;
        }

        // The completion-handler form rather than -openURL:, which the 26 SDK deprecates.
        NSWorkspaceOpenConfiguration *configuration = [NSWorkspaceOpenConfiguration configuration];
        [[NSWorkspace sharedWorkspace] openURL:url configuration:configuration completionHandler:nil];
        return RD_OK;
    }
}

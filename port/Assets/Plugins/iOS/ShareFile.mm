// iOS port: shows the system share sheet (AirDrop, Save to Files, iCloud Drive, ...) for one file.
// Used by IOSPort.SaveExport to get save files out of the app.
#import <UIKit/UIKit.h>

extern "C" UIViewController* UnityGetGLViewController(void);

extern "C" void IOSPortShareFile(const char* path)
{
    NSURL* url = [NSURL fileURLWithPath:[NSString stringWithUTF8String:path]];
    UIActivityViewController* share = [[UIActivityViewController alloc] initWithActivityItems:@[url] applicationActivities:nil];
    UIViewController* root = UnityGetGLViewController();
    // Required on iPad, ignored on iPhone.
    share.popoverPresentationController.sourceView = root.view;
    share.popoverPresentationController.sourceRect = CGRectMake(CGRectGetMidX(root.view.bounds), CGRectGetMidY(root.view.bounds), 1, 1);
    [root presentViewController:share animated:YES completion:nil];
}

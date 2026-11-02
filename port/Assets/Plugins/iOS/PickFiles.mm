// iOS port: shows the Files picker (iCloud Drive, On My iPhone, ...) so save files can be brought into the app.
// Used by IOSPort.SaveImport, which gets the picked files' paths, one per line, or "" if nothing was picked.
#import <UIKit/UIKit.h>

extern "C" UIViewController* UnityGetGLViewController(void);
extern "C" void UnitySendMessage(const char* obj, const char* method, const char* msg);

@interface IOSPortFilePicker : NSObject <UIDocumentPickerDelegate>
@property (nonatomic, copy) NSString* receiver;
@end

@implementation IOSPortFilePicker

- (void)documentPicker:(UIDocumentPickerViewController*)controller didPickDocumentsAtURLs:(NSArray<NSURL*>*)urls
{
    NSMutableArray<NSString*>* paths = [NSMutableArray array];
    for (NSURL* url in urls)
        [paths addObject:url.path];
    UnitySendMessage(self.receiver.UTF8String, "OnFilesPicked", [paths componentsJoinedByString:@"\n"].UTF8String);
}

- (void)documentPickerWasCancelled:(UIDocumentPickerViewController*)controller
{
    UnitySendMessage(self.receiver.UTF8String, "OnFilesPicked", "");
}

@end

static IOSPortFilePicker* picker;

extern "C" void IOSPortPickFiles(const char* receiver)
{
    if (picker == nil)
        picker = [IOSPortFilePicker new];
    picker.receiver = [NSString stringWithUTF8String:receiver];
    // Import mode copies the picked files into the app's tmp/<bundle id>-Inbox folder.
    UIDocumentPickerViewController* pick = [[UIDocumentPickerViewController alloc]
        initWithDocumentTypes:@[@"public.zip-archive", @"public.data"] inMode:UIDocumentPickerModeImport];
    pick.delegate = picker;
    pick.allowsMultipleSelection = YES;
    UIViewController* root = UnityGetGLViewController();
    [root presentViewController:pick animated:YES completion:nil];
}

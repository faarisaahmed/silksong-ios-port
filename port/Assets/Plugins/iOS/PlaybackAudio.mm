// iOS port: Unity starts the audio session as "ambient", which the ring/silent switch mutes. Games normally use
// "playback" so they're heard in silent mode; mixing with others keeps the player's own music playing, as before.
// Unity can set the category again when the app returns or after an interruption (a call, Siri), so it's
// re-applied then too.
#import <AVFoundation/AVFoundation.h>
#import <UIKit/UIKit.h>

static void IOSPortApplyPlaybackCategory()
{
    AVAudioSession* session = [AVAudioSession sharedInstance];
    if (session.category == AVAudioSessionCategoryPlayback && (session.categoryOptions & AVAudioSessionCategoryOptionMixWithOthers))
        return;
    [session setCategory:AVAudioSessionCategoryPlayback withOptions:AVAudioSessionCategoryOptionMixWithOthers error:nil];
}

extern "C" void IOSPortUsePlaybackAudio()
{
    IOSPortApplyPlaybackCategory();
    static bool observing = false;
    if (observing)
        return;
    observing = true;
    // Queued on the main queue, so these run after Unity's own handlers for the same events.
    for (NSNotificationName name in @[UIApplicationDidBecomeActiveNotification, AVAudioSessionInterruptionNotification])
        [[NSNotificationCenter defaultCenter] addObserverForName:name object:nil queue:[NSOperationQueue mainQueue]
            usingBlock:^(NSNotification*) { dispatch_async(dispatch_get_main_queue(), ^{ IOSPortApplyPlaybackCategory(); }); }];
}

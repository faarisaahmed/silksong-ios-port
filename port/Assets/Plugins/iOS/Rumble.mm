// iOS port: plays the game's rumble (two motor strengths, updated every frame) on the iPhone's Taptic Engine
// or on a connected controller's motors. Used by IOSPort.Rumble.
#import <CoreHaptics/CoreHaptics.h>
#import <GameController/GameController.h>

// One haptic engine playing a looping continuous buzz whose strength is changed while it plays.
@interface IOSPortBuzz : NSObject
@property (nonatomic, strong) CHHapticEngine* engine;
@property (nonatomic, strong) id<CHHapticAdvancedPatternPlayer> player;
@property (nonatomic) BOOL broken;
@property (nonatomic) BOOL playing;
@end

@implementation IOSPortBuzz

- (instancetype)initWithEngine:(CHHapticEngine*)engine
{
    if (!(self = [super init]) || engine == nil)
        return nil;
    _engine = engine;
    engine.playsHapticsOnly = YES;
    engine.autoShutdownEnabled = YES;
    __weak IOSPortBuzz* weakSelf = self;
    // The engine stops when the app goes to the background and resets if the haptic server restarts.
    engine.stoppedHandler = ^(CHHapticEngineStoppedReason reason) { weakSelf.playing = NO; };
    engine.resetHandler = ^{ weakSelf.player = nil; weakSelf.playing = NO; };
    return self;
}

- (void)setIntensity:(float)intensity sharpness:(float)sharpness
{
    if (_broken)
        return;
    NSError* error = nil;
    if (intensity <= 0.01f)
    {
        if (_playing)
            [_player stopAtTime:CHHapticTimeImmediate error:&error];
        _playing = NO;
        return;
    }
    if (_player == nil)
    {
        CHHapticEvent* buzz = [[CHHapticEvent alloc] initWithEventType:CHHapticEventTypeHapticContinuous parameters:@[
            [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticIntensity value:1],
            [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticSharpness value:0.5f],
        ] relativeTime:0 duration:30];
        CHHapticPattern* pattern = [[CHHapticPattern alloc] initWithEvents:@[buzz] parameters:@[] error:&error];
        _player = pattern != nil ? [_engine createAdvancedPlayerWithPattern:pattern error:&error] : nil;
        if (_player == nil)
        {
            NSLog(@"[IOSPort] rumble: couldn't make a haptic player: %@", error);
            _broken = YES;
            return;
        }
        _player.loopEnabled = YES;
        _player.loopEnd = 30;
    }
    [_player sendParameters:@[
        [[CHHapticDynamicParameter alloc] initWithParameterID:CHHapticDynamicParameterIDHapticIntensityControl value:intensity relativeTime:0],
        [[CHHapticDynamicParameter alloc] initWithParameterID:CHHapticDynamicParameterIDHapticSharpnessControl value:sharpness relativeTime:0],
    ] atTime:CHHapticTimeImmediate error:&error];
    if (!_playing)
    {
        if ([_engine startAndReturnError:&error] && [_player startAtTime:CHHapticTimeImmediate error:&error])
            _playing = YES;
        else
            NSLog(@"[IOSPort] rumble: couldn't start: %@", error);
    }
}

@end

static IOSPortBuzz* phoneBuzz;
static BOOL phoneChecked;
static __weak GCController* buzzController;
static IOSPortBuzz* leftBuzz;   // the controller's large motor (or its only one)
static IOSPortBuzz* rightBuzz;  // the small motor, when the controller has separate handles

static void StopController()
{
    [leftBuzz setIntensity:0 sharpness:0];
    [rightBuzz setIntensity:0 sharpness:0];
    leftBuzz = rightBuzz = nil;
    buzzController = nil;
}

static GCController* CurrentController()
{
    GCController* c = GCController.current;
    if (c != nil && c.haptics != nil)
        return c;
    for (GCController* each in GCController.controllers)
        if (each.haptics != nil)
            return each;
    return nil;
}

// small/large: the game's motor strengths (0-1). toController: rumble the controller instead of the phone.
extern "C" void IOSPortRumble(float small, float large, int toController)
{
    if (toController)
    {
        [phoneBuzz setIntensity:0 sharpness:0];
        GCController* c = CurrentController();
        if (c != buzzController)
        {
            StopController();
            buzzController = c;
            NSSet<GCHapticsLocality>* where = c.haptics.supportedLocalities;
            if ([where containsObject:GCHapticsLocalityLeftHandle] && [where containsObject:GCHapticsLocalityRightHandle])
            {
                leftBuzz = [[IOSPortBuzz alloc] initWithEngine:[c.haptics createEngineWithLocality:GCHapticsLocalityLeftHandle]];
                rightBuzz = [[IOSPortBuzz alloc] initWithEngine:[c.haptics createEngineWithLocality:GCHapticsLocalityRightHandle]];
            }
            else if (c != nil)
                leftBuzz = [[IOSPortBuzz alloc] initWithEngine:[c.haptics createEngineWithLocality:GCHapticsLocalityDefault]];
        }
        if (rightBuzz != nil)
        {
            [leftBuzz setIntensity:large sharpness:-0.3f];
            [rightBuzz setIntensity:small sharpness:0.3f];
        }
        else
            [leftBuzz setIntensity:fmaxf(small, large) sharpness:0];
        return;
    }
    StopController();
    if (!phoneChecked)
    {
        phoneChecked = YES;
        if (CHHapticEngine.capabilitiesForHardware.supportsHaptics)
        {
            NSError* error = nil;
            phoneBuzz = [[IOSPortBuzz alloc] initWithEngine:[[CHHapticEngine alloc] initAndReturnError:&error]];
            if (phoneBuzz == nil)
                NSLog(@"[IOSPort] rumble: no haptic engine: %@", error);
        }
    }
    // The large motor is the heavy, low rumble; the small one is the light buzz.
    [phoneBuzz setIntensity:fmaxf(small, large) sharpness:(small - large) * 0.4f];
}

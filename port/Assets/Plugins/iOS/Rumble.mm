// iOS port: plays the game's rumble (two motor strengths, updated every frame) on the iPhone's Taptic Engine
// or on a connected controller's motors. Used by IOSPort.Rumble.
#import <CoreHaptics/CoreHaptics.h>
#import <GameController/GameController.h>
#import <QuartzCore/QuartzCore.h>

// The phone: one haptic engine playing a looping continuous buzz whose strength is changed while it plays.
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

// Messages for IOSPort.Rumble to write to Documents/Logs/rumble.txt (there's no other way to see them on a phone).
static NSMutableString* pendingLog;

static void Log(NSString* message)
{
    NSLog(@"[IOSPort] rumble: %@", message);
    if (pendingLog == nil)
        pendingLog = [NSMutableString string];
    if (pendingLog.length < 4000)
        [pendingLog appendFormat:@"%@\n", message];
}

extern "C" char* IOSPortRumbleLog()
{
    if (pendingLog.length == 0)
        return NULL;
    const char* text = pendingLog.UTF8String;
    char* copy = (char*)malloc(strlen(text) + 1); // freed by the IL2CPP marshaller
    strcpy(copy, text);
    [pendingLog setString:@""];
    return copy;
}

// One motor (or the whole controller) of a game controller. Controllers get a short buzz at the current strength
// a few times a second rather than one long buzz with its strength changed as it plays, which not every
// controller's haptics support.
@interface IOSPortMotor : NSObject
@property (nonatomic, strong) CHHapticEngine* engine;
@property (nonatomic, strong) id<CHHapticPatternPlayer> player;
@property (nonatomic, copy) NSString* name;
@property (nonatomic) float lastIntensity;
@property (nonatomic) double lastStart;
@property (nonatomic) BOOL started;
@property (nonatomic) int failures;
@end

@implementation IOSPortMotor

- (instancetype)initWithController:(GCController*)c locality:(GCHapticsLocality)locality
{
    if (!(self = [super init]))
        return nil;
    _name = [NSString stringWithFormat:@"%@ (%@)", c.vendorName, locality];
    _engine = [c.haptics createEngineWithLocality:locality];
    if (_engine == nil)
    {
        Log([NSString stringWithFormat:@"%@: no haptic engine", _name]);
        return nil;
    }
    __weak IOSPortMotor* weakSelf = self;
    _engine.stoppedHandler = ^(CHHapticEngineStoppedReason reason) { weakSelf.started = NO; };
    _engine.resetHandler = ^{ weakSelf.started = NO; weakSelf.player = nil; };
    Log([NSString stringWithFormat:@"%@: haptic engine ready", _name]);
    return self;
}

- (void)stop
{
    if (_player != nil)
        [_player stopAtTime:CHHapticTimeImmediate error:nil];
    _player = nil;
    _lastIntensity = 0;
}

- (void)setIntensity:(float)intensity sharpness:(float)sharpness
{
    if (_failures > 20)
        return;
    if (intensity <= 0.01f)
    {
        [self stop];
        return;
    }
    double now = CACurrentMediaTime();
    // A new buzz when the strength changes noticeably, or before the last one runs out.
    if (_player != nil && fabsf(intensity - _lastIntensity) < 0.05f && now - _lastStart < 0.08)
        return;
    NSError* error = nil;
    if (!_started)
    {
        if (![_engine startAndReturnError:&error])
        {
            _failures++;
            Log([NSString stringWithFormat:@"%@: couldn't start: %@", _name, error]);
            return;
        }
        _started = YES;
    }
    CHHapticEvent* buzz = [[CHHapticEvent alloc] initWithEventType:CHHapticEventTypeHapticContinuous parameters:@[
        [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticIntensity value:fminf(intensity, 1)],
        [[CHHapticEventParameter alloc] initWithParameterID:CHHapticEventParameterIDHapticSharpness value:sharpness],
    ] relativeTime:0 duration:0.12];
    CHHapticPattern* pattern = [[CHHapticPattern alloc] initWithEvents:@[buzz] parameters:@[] error:&error];
    id<CHHapticPatternPlayer> player = pattern != nil ? [_engine createPlayerWithPattern:pattern error:&error] : nil;
    if (player == nil || ![player startAtTime:CHHapticTimeImmediate error:&error])
    {
        _failures++;
        _started = NO;
        Log([NSString stringWithFormat:@"%@: couldn't play: %@", _name, error]);
        return;
    }
    if (_player != nil)
        [_player stopAtTime:CHHapticTimeImmediate error:nil];
    _player = player;
    _lastIntensity = intensity;
    _lastStart = now;
}

@end

static IOSPortBuzz* phoneBuzz;
static BOOL phoneChecked;
static __weak GCController* motorController;
static IOSPortMotor* largeMotor; // the left handle, or the whole controller when it has no separate handles
static IOSPortMotor* smallMotor; // the right handle

static void StopController()
{
    [largeMotor stop];
    [smallMotor stop];
    largeMotor = smallMotor = nil;
    motorController = nil;
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
        if (c != motorController)
        {
            StopController();
            motorController = c;
            if (c == nil)
            {
                static BOOL warned;
                if (!warned)
                    Log([NSString stringWithFormat:@"no controller with haptics (%lu connected)", (unsigned long)GCController.controllers.count]);
                warned = YES;
                return;
            }
            NSSet<GCHapticsLocality>* where = c.haptics.supportedLocalities;
            Log([NSString stringWithFormat:@"%@ (%@): haptics at %@", c.vendorName, c.productCategory, [where.allObjects componentsJoinedByString:@", "]]);
            if ([where containsObject:GCHapticsLocalityLeftHandle] && [where containsObject:GCHapticsLocalityRightHandle])
            {
                largeMotor = [[IOSPortMotor alloc] initWithController:c locality:GCHapticsLocalityLeftHandle];
                smallMotor = [[IOSPortMotor alloc] initWithController:c locality:GCHapticsLocalityRightHandle];
            }
            if (largeMotor == nil || smallMotor == nil)
            {
                smallMotor = nil;
                largeMotor = [[IOSPortMotor alloc] initWithController:c locality:GCHapticsLocalityDefault];
            }
        }
        if (smallMotor != nil)
        {
            [largeMotor setIntensity:large sharpness:0.2f];
            [smallMotor setIntensity:small sharpness:0.8f];
        }
        else
            [largeMotor setIntensity:fmaxf(small, large) sharpness:0.5f];
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
                Log([NSString stringWithFormat:@"no haptic engine: %@", error]);
        }
    }
    // The large motor is the heavy, low rumble; the small one is the light buzz.
    [phoneBuzz setIntensity:fmaxf(small, large) sharpness:(small - large) * 0.4f];
}

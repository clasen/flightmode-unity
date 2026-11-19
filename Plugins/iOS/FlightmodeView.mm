// Plays Flightmode HTML creatives in a WKWebView above the Unity view.
// The package is served from disk on flightmode://creative/ and every other load is blocked.
// The native layer owns the progress bar, the close button and the play time.

#import <UIKit/UIKit.h>
#import <WebKit/WebKit.h>
#import <QuartzCore/QuartzCore.h>
#import <Network/Network.h>
#include <atomic>

extern UIViewController* UnityGetGLViewController(void);
extern void UnitySendMessage(const char* obj, const char* method, const char* msg);

static NSString* const FMScheme = @"flightmode";
static NSString* const FMHost = @"creative";
static NSString* const FMPolicy = @"default-src 'self' 'unsafe-inline' 'unsafe-eval' data: blob:; connect-src 'self'";
static NSString* const FMRules = @"[{\"trigger\":{\"url-filter\":\".*\"},\"action\":{\"type\":\"block\"}},"
                                  "{\"trigger\":{\"url-filter\":\"^flightmode://\"},\"action\":{\"type\":\"ignore-previous-rules\"}}]";
static const CGFloat FMMargin = 12;
static const CGFloat FMBarHeight = 4;
static const CGFloat FMCloseSize = 32;
static const CGFloat FMStepGap = 4;

static NSString* FMContentType(NSString* path) {
    static NSDictionary<NSString*, NSString*>* types;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        types = @{
            @"html": @"text/html; charset=utf-8", @"js": @"text/javascript; charset=utf-8", @"mjs": @"text/javascript; charset=utf-8",
            @"css": @"text/css; charset=utf-8", @"json": @"application/json", @"svg": @"image/svg+xml", @"png": @"image/png",
            @"jpg": @"image/jpeg", @"jpeg": @"image/jpeg", @"webp": @"image/webp", @"gif": @"image/gif", @"woff2": @"font/woff2",
            @"woff": @"font/woff", @"ttf": @"font/ttf", @"mp4": @"video/mp4", @"webm": @"video/webm", @"mp3": @"audio/mpeg", @"ogg": @"audio/ogg"
        };
    });
    return types[path.pathExtension.lowercaseString] ?: @"application/octet-stream";
}

@interface FMSchemeHandler : NSObject <WKURLSchemeHandler>
@property (nonatomic, copy) NSString* root;
@property (nonatomic, copy) NSString* entryPath;
@property (nonatomic, copy) NSString* entryHtml;
@end

@implementation FMSchemeHandler

- (void)webView:(WKWebView*)webView startURLSchemeTask:(id<WKURLSchemeTask>)task {
    NSURL* url = task.request.URL;
    NSString* path = [url.path hasPrefix:@"/"] ? [url.path substringFromIndex:1] : url.path;
    NSData* data = nil;
    NSInteger status = 200;
    if (![url.host isEqualToString:FMHost]) {
        status = 403;
    } else if ([path isEqualToString:self.entryPath]) {
        data = [self.entryHtml dataUsingEncoding:NSUTF8StringEncoding];
    } else {
        NSString* root = [self.root stringByStandardizingPath];
        NSString* full = [[root stringByAppendingPathComponent:path] stringByStandardizingPath];
        if (![full hasPrefix:[root stringByAppendingString:@"/"]]) status = 403;
        else if (!(data = [NSData dataWithContentsOfFile:full])) status = 404;
    }
    NSDictionary* headers = @{
        @"Content-Type": FMContentType(path),
        @"Content-Security-Policy": FMPolicy,
        @"Cache-Control": @"no-store"
    };
    NSHTTPURLResponse* response = [[NSHTTPURLResponse alloc] initWithURL:url statusCode:status HTTPVersion:@"HTTP/1.1" headerFields:headers];
    [task didReceiveResponse:response];
    if (data) [task didReceiveData:data];
    [task didFinish];
}

- (void)webView:(WKWebView*)webView stopURLSchemeTask:(id<WKURLSchemeTask>)task {
}

@end

// Breaks the WKUserContentController -> handler retain cycle.
@interface FMWeakHandler : NSObject <WKScriptMessageHandler>
@property (nonatomic, weak) id<WKScriptMessageHandler> target;
@end

@implementation FMWeakHandler
- (void)userContentController:(WKUserContentController*)controller didReceiveScriptMessage:(WKScriptMessage*)message {
    [self.target userContentController:controller didReceiveScriptMessage:message];
}
@end

@interface FMCloseButton : UIControl
@end

@implementation FMCloseButton

- (instancetype)init {
    if ((self = [super initWithFrame:CGRectMake(0, 0, FMCloseSize, FMCloseSize)])) {
        self.backgroundColor = [UIColor colorWithWhite:0 alpha:0.55];
        self.layer.cornerRadius = 6;
        CGFloat inset = FMCloseSize * 0.3;
        UIBezierPath* path = [UIBezierPath bezierPath];
        [path moveToPoint:CGPointMake(inset, inset)];
        [path addLineToPoint:CGPointMake(FMCloseSize - inset, FMCloseSize - inset)];
        [path moveToPoint:CGPointMake(FMCloseSize - inset, inset)];
        [path addLineToPoint:CGPointMake(inset, FMCloseSize - inset)];
        CAShapeLayer* cross = [CAShapeLayer layer];
        cross.path = path.CGPath;
        cross.strokeColor = UIColor.whiteColor.CGColor;
        cross.lineWidth = 2.5;
        cross.lineCap = kCALineCapRound;
        [self.layer addSublayer:cross];
    }
    return self;
}

@end

@interface FMAd : NSObject <WKScriptMessageHandler, WKNavigationDelegate, WKUIDelegate>
@property (nonatomic, copy) NSString* slot;
@property (nonatomic, copy) NSString* callbackObject;
@property (nonatomic) int durationMs;
@property (nonatomic) int closeAfterMs;
@property (nonatomic, strong) UIView* container;
@property (nonatomic, strong) WKWebView* webView;
@property (nonatomic, strong) FMSchemeHandler* scheme;
@property (nonatomic, strong) NSMutableArray<UIView*>* fills;
@property (nonatomic, strong) FMCloseButton* closeButton;
@property (nonatomic, strong) CADisplayLink* displayLink;
@property (nonatomic) BOOL started;
@property (nonatomic) BOOL paused;
@property (nonatomic) BOOL completed;
@property (nonatomic) BOOL finished;
@property (nonatomic) CFTimeInterval accumulated;
@property (nonatomic) CFTimeInterval resumedAt;
- (void)finishClosedByUser:(BOOL)closedByUser completed:(BOOL)completed;
- (void)setPaused:(BOOL)paused;
@end

static NSMutableDictionary<NSString*, FMAd*>* FMAds;
static WKContentRuleList* FMRuleList;

@implementation FMAd

- (void)presentWithRoot:(NSString*)root entryPath:(NSString*)entryPath entryHtml:(NSString*)entryHtml progressSteps:(int)steps
         readyTimeoutMs:(int)readyTimeoutMs bannerTop:(BOOL)bannerTop bannerWidth:(int)bannerWidth bannerHeight:(int)bannerHeight {
    UIView* host = UnityGetGLViewController().view;
    BOOL interstitial = self.durationMs > 0;

    self.scheme = [FMSchemeHandler new];
    self.scheme.root = root;
    self.scheme.entryPath = entryPath;
    self.scheme.entryHtml = entryHtml;
    WKWebViewConfiguration* configuration = [WKWebViewConfiguration new];
    configuration.websiteDataStore = [WKWebsiteDataStore nonPersistentDataStore];
    configuration.allowsInlineMediaPlayback = YES;
    configuration.mediaTypesRequiringUserActionForPlayback = WKAudiovisualMediaTypeNone;
    [configuration setURLSchemeHandler:self.scheme forURLScheme:FMScheme];
    [configuration.userContentController addContentRuleList:FMRuleList];
    FMWeakHandler* handler = [FMWeakHandler new];
    handler.target = self;
    [configuration.userContentController addScriptMessageHandler:handler name:@"fm"];

    self.container = [UIView new];
    self.container.translatesAutoresizingMaskIntoConstraints = NO;
    self.container.backgroundColor = interstitial ? UIColor.blackColor : UIColor.clearColor;
    [host addSubview:self.container];
    UILayoutGuide* safe = host.safeAreaLayoutGuide;
    if (interstitial) {
        [NSLayoutConstraint activateConstraints:@[
            [self.container.topAnchor constraintEqualToAnchor:host.topAnchor],
            [self.container.bottomAnchor constraintEqualToAnchor:host.bottomAnchor],
            [self.container.leadingAnchor constraintEqualToAnchor:host.leadingAnchor],
            [self.container.trailingAnchor constraintEqualToAnchor:host.trailingAnchor]
        ]];
    } else {
        [NSLayoutConstraint activateConstraints:@[
            [self.container.widthAnchor constraintEqualToConstant:bannerWidth],
            [self.container.heightAnchor constraintEqualToConstant:bannerHeight],
            [self.container.centerXAnchor constraintEqualToAnchor:safe.centerXAnchor],
            bannerTop ? [self.container.topAnchor constraintEqualToAnchor:safe.topAnchor]
                      : [self.container.bottomAnchor constraintEqualToAnchor:safe.bottomAnchor]
        ]];
    }

    self.webView = [[WKWebView alloc] initWithFrame:CGRectZero configuration:configuration];
    self.webView.translatesAutoresizingMaskIntoConstraints = NO;
    self.webView.opaque = NO;
    self.webView.backgroundColor = UIColor.clearColor;
    self.webView.scrollView.scrollEnabled = NO;
    self.webView.scrollView.bounces = NO;
    self.webView.scrollView.contentInsetAdjustmentBehavior = UIScrollViewContentInsetAdjustmentNever;
    self.webView.navigationDelegate = self;
    self.webView.UIDelegate = self;
    [self.container addSubview:self.webView];
    UILayoutGuide* frame = interstitial ? self.container.safeAreaLayoutGuide : nil;
    [NSLayoutConstraint activateConstraints:@[
        [self.webView.topAnchor constraintEqualToAnchor:frame ? frame.topAnchor : self.container.topAnchor],
        [self.webView.bottomAnchor constraintEqualToAnchor:frame ? frame.bottomAnchor : self.container.bottomAnchor],
        [self.webView.leadingAnchor constraintEqualToAnchor:frame ? frame.leadingAnchor : self.container.leadingAnchor],
        [self.webView.trailingAnchor constraintEqualToAnchor:frame ? frame.trailingAnchor : self.container.trailingAnchor]
    ]];

    self.fills = [NSMutableArray array];
    if (interstitial) [self addChromeWithSteps:MAX(1, steps)];

    NSString* entry = [NSString stringWithFormat:@"%@://%@/%@", FMScheme, FMHost, entryPath];
    [self.webView loadRequest:[NSURLRequest requestWithURL:[NSURL URLWithString:entry]]];

    __weak FMAd* weakSelf = self;
    dispatch_after(dispatch_time(DISPATCH_TIME_NOW, (int64_t)readyTimeoutMs * NSEC_PER_MSEC), dispatch_get_main_queue(), ^{
        FMAd* ad = weakSelf;
        if (ad && !ad.started) [ad finishClosedByUser:NO completed:NO];
    });
}

- (void)addChromeWithSteps:(int)steps {
    UILayoutGuide* safe = self.container.safeAreaLayoutGuide;
    UIStackView* bar = [UIStackView new];
    bar.translatesAutoresizingMaskIntoConstraints = NO;
    bar.axis = UILayoutConstraintAxisHorizontal;
    bar.distribution = UIStackViewDistributionFillEqually;
    bar.spacing = FMStepGap;
    for (int i = 0; i < steps; i++) {
        UIView* track = [UIView new];
        track.backgroundColor = [UIColor colorWithWhite:1 alpha:0.35];
        track.layer.cornerRadius = FMBarHeight / 2;
        track.clipsToBounds = YES;
        UIView* fill = [[UIView alloc] initWithFrame:CGRectZero];
        fill.backgroundColor = UIColor.whiteColor;
        [track addSubview:fill];
        [bar addArrangedSubview:track];
        [self.fills addObject:fill];
    }
    [self.container addSubview:bar];
    self.closeButton = [FMCloseButton new];
    self.closeButton.translatesAutoresizingMaskIntoConstraints = NO;
    self.closeButton.hidden = self.closeAfterMs > 0;
    [self.closeButton addTarget:self action:@selector(closeTapped) forControlEvents:UIControlEventTouchUpInside];
    [self.container addSubview:self.closeButton];
    [NSLayoutConstraint activateConstraints:@[
        [bar.topAnchor constraintEqualToAnchor:safe.topAnchor constant:FMMargin],
        [bar.leadingAnchor constraintEqualToAnchor:safe.leadingAnchor constant:FMMargin],
        [bar.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor constant:-FMMargin],
        [bar.heightAnchor constraintEqualToConstant:FMBarHeight],
        [self.closeButton.topAnchor constraintEqualToAnchor:bar.bottomAnchor constant:FMMargin],
        [self.closeButton.trailingAnchor constraintEqualToAnchor:safe.trailingAnchor constant:-FMMargin],
        [self.closeButton.widthAnchor constraintEqualToConstant:FMCloseSize],
        [self.closeButton.heightAnchor constraintEqualToConstant:FMCloseSize]
    ]];
}

- (CFTimeInterval)elapsedMs {
    CFTimeInterval running = self.started && !self.paused && !self.completed ? CACurrentMediaTime() - self.resumedAt : 0;
    return (self.accumulated + running) * 1000.0;
}

- (void)start {
    if (self.started || self.finished) return;
    self.started = YES;
    self.resumedAt = CACurrentMediaTime();
    [self emit:@"onStart" argument:nil];
    if (self.durationMs <= 0) return;
    self.displayLink = [CADisplayLink displayLinkWithTarget:self selector:@selector(tick)];
    [self.displayLink addToRunLoop:NSRunLoop.mainRunLoop forMode:NSRunLoopCommonModes];
}

- (void)tick {
    if (self.paused || self.finished) return;
    CFTimeInterval elapsed = [self elapsedMs];
    if (elapsed >= self.closeAfterMs) self.closeButton.hidden = NO;
    if (elapsed >= self.durationMs) [self complete];
    else [self setProgress:elapsed / self.durationMs];
}

- (void)setProgress:(double)share {
    double progress = MIN(1.0, share) * self.fills.count;
    for (NSUInteger i = 0; i < self.fills.count; i++) {
        UIView* fill = self.fills[i];
        CGRect bounds = fill.superview.bounds;
        fill.frame = CGRectMake(0, 0, bounds.size.width * MAX(0.0, MIN(1.0, progress - i)), bounds.size.height);
    }
}

// The creative reached its end: the clock stops and the ad stays up until the user closes it.
- (void)complete {
    self.accumulated = [self elapsedMs] / 1000.0;
    self.completed = YES;
    [self.displayLink invalidate];
    self.displayLink = nil;
    [self setProgress:1.0];
    self.closeButton.hidden = NO;
}

// A close after the creative played to its end is a completion, not a skip.
- (void)closeTapped {
    [self finishClosedByUser:!self.completed completed:self.completed];
}

- (void)setPaused:(BOOL)paused {
    if (!self.started || self.finished || paused == _paused) return;
    if (paused) self.accumulated = [self elapsedMs] / 1000.0;
    else self.resumedAt = CACurrentMediaTime();
    _paused = paused;
    [self emit:@"onPause" argument:paused ? @"true" : @"false"];
}

- (void)emit:(NSString*)name argument:(NSString*)argument {
    NSString* script = [NSString stringWithFormat:@"window.__flightmodeEmit&&window.__flightmodeEmit('%@'%@)", name,
                        argument ? [@"," stringByAppendingString:argument] : @""];
    [self.webView evaluateJavaScript:script completionHandler:nil];
}

- (void)send:(NSDictionary*)event {
    NSMutableDictionary* payload = [event mutableCopy];
    payload[@"slot"] = self.slot;
    NSData* json = [NSJSONSerialization dataWithJSONObject:payload options:0 error:nil];
    NSString* text = [[NSString alloc] initWithData:json encoding:NSUTF8StringEncoding];
    UnitySendMessage(self.callbackObject.UTF8String, "OnNativeEvent", text.UTF8String);
}

- (void)finishClosedByUser:(BOOL)closedByUser completed:(BOOL)completed {
    if (self.finished) return;
    long long viewMs = (long long)[self elapsedMs];
    self.finished = YES;
    [self.displayLink invalidate];
    self.displayLink = nil;
    [self.webView.configuration.userContentController removeScriptMessageHandlerForName:@"fm"];
    [self.webView stopLoading];
    [self.container removeFromSuperview];
    if (FMAds[self.slot] == self) [FMAds removeObjectForKey:self.slot];
    [self send:@{ @"t": @"closed", @"viewMs": @(viewMs), @"closedByUser": @(closedByUser), @"completed": @(completed || self.completed) }];
}

- (void)userContentController:(WKUserContentController*)controller didReceiveScriptMessage:(WKScriptMessage*)message {
    if (![message.body isKindOfClass:NSDictionary.class] || self.finished) return;
    NSDictionary* body = message.body;
    NSString* type = body[@"t"];
    if ([type isEqualToString:@"ready"]) {
        [self start];
    } else if ([type isEqualToString:@"open"]) {
        if (self.started && [body[@"id"] isKindOfClass:NSString.class]) [self send:@{ @"t": @"open", @"id": body[@"id"] }];
    } else if ([type isEqualToString:@"done"]) {
        if (self.started && self.durationMs > 0 && !self.completed) [self complete];
    }
}

- (void)webView:(WKWebView*)webView decidePolicyForNavigationAction:(WKNavigationAction*)action
    decisionHandler:(void (^)(WKNavigationActionPolicy))decisionHandler {
    BOOL local = [action.request.URL.scheme isEqualToString:FMScheme];
    decisionHandler(local ? WKNavigationActionPolicyAllow : WKNavigationActionPolicyCancel);
}

- (WKWebView*)webView:(WKWebView*)webView createWebViewWithConfiguration:(WKWebViewConfiguration*)configuration
    forNavigationAction:(WKNavigationAction*)action windowFeatures:(WKWindowFeatures*)features {
    return nil;
}

- (void)webViewWebContentProcessDidTerminate:(WKWebView*)webView {
    [self finishClosedByUser:NO completed:NO];
}

@end

// iOS has no public airplane mode flag. Only a denial of cellular or Wi-Fi data to the app is
// unambiguous; a path with no interfaces looks the same in airplane mode and without signal.
static int FMPathState(nw_path_t path) {
    if (nw_path_get_status(path) == nw_path_status_satisfied) return 0;
    if (@available(iOS 14.2, *)) {
        nw_path_unsatisfied_reason_t reason = nw_path_get_unsatisfied_reason(path);
        if (reason == nw_path_unsatisfied_reason_cellular_denied || reason == nw_path_unsatisfied_reason_wifi_denied) return 2;
    }
    return 1;
}

static std::atomic<int> FMConnectivityState(-1);

static NSString* FMString(const char* value) {
    return value ? [NSString stringWithUTF8String:value] : @"";
}

extern "C" {

void FMShow(const char* slot, const char* root, const char* entryPath, const char* entryHtml, int durationMs, int closeAfterMs,
            int progressSteps, int readyTimeoutMs, bool bannerTop, int bannerWidthDp, int bannerHeightDp, const char* callbackObject) {
    if (!FMAds) FMAds = [NSMutableDictionary dictionary];
    FMAd* ad = [FMAd new];
    ad.slot = FMString(slot);
    ad.callbackObject = FMString(callbackObject);
    ad.durationMs = durationMs;
    ad.closeAfterMs = closeAfterMs;
    [FMAds[ad.slot] finishClosedByUser:NO completed:NO];
    FMAds[ad.slot] = ad;
    NSString* rootPath = FMString(root);
    NSString* entry = FMString(entryPath);
    NSString* html = FMString(entryHtml);
    void (^present)(void) = ^{
        if (FMAds[ad.slot] != ad) return;
        [ad presentWithRoot:rootPath entryPath:entry entryHtml:html progressSteps:progressSteps readyTimeoutMs:readyTimeoutMs
                  bannerTop:bannerTop bannerWidth:bannerWidthDp bannerHeight:bannerHeightDp];
    };
    if (FMRuleList) {
        present();
        return;
    }
    [WKContentRuleListStore.defaultStore compileContentRuleListForIdentifier:@"flightmode-offline" encodedContentRuleList:FMRules
        completionHandler:^(WKContentRuleList* list, NSError* error) {
            if (!list) {
                NSLog(@"Flightmode: content rules failed to compile: %@", error);
                [ad finishClosedByUser:NO completed:NO];
                return;
            }
            FMRuleList = list;
            present();
        }];
}

void FMHide(const char* slot) {
    [FMAds[FMString(slot)] finishClosedByUser:NO completed:NO];
}

void FMSetPaused(bool paused) {
    for (FMAd* ad in FMAds.allValues) [ad setPaused:paused];
}

char* FMLocale(void) {
    NSString* locale = NSLocale.preferredLanguages.firstObject ?: NSLocale.currentLocale.localeIdentifier;
    return strdup(locale.UTF8String);
}

// 0 online, 1 offline, 2 offline by the user, -1 until the path monitor reports its first path.
int FMConnectivity(void) {
    static nw_path_monitor_t monitor;
    static dispatch_once_t once;
    dispatch_once(&once, ^{
        monitor = nw_path_monitor_create();
        nw_path_monitor_set_queue(monitor, dispatch_queue_create("ad.flightmode.path", DISPATCH_QUEUE_SERIAL));
        nw_path_monitor_set_update_handler(monitor, ^(nw_path_t path) {
            FMConnectivityState.store(FMPathState(path));
        });
        nw_path_monitor_start(monitor);
    });
    return FMConnectivityState.load();
}

}

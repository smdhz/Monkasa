#import <AppKit/AppKit.h>
#import <QuickLookThumbnailing/QuickLookThumbnailing.h>
#import <dispatch/dispatch.h>
#import <sys/stat.h>

int monkasa_is_dataless(const char *path)
{
    if (path == NULL) {
        return 0;
    }

    struct stat fileInfo;
    if (stat(path, &fileInfo) != 0) {
        return 0;
    }

#ifdef SF_DATALESS
    return (fileInfo.st_flags & SF_DATALESS) != 0 ? 1 : 0;
#else
    return 0;
#endif
}

int monkasa_generate_thumbnail(
    const char *path,
    int width,
    int height,
    double scale,
    unsigned char **outputBytes,
    long *outputLength)
{
    if (path == NULL || outputBytes == NULL || outputLength == NULL || width <= 0 || height <= 0) {
        return 0;
    }

    *outputBytes = NULL;
    *outputLength = 0;

    @autoreleasepool {
        NSString *filePath = [NSString stringWithUTF8String:path];
        if (filePath == nil) {
            return 0;
        }

        NSURL *fileUrl = [NSURL fileURLWithPath:filePath];
        QLThumbnailGenerationRequest *request = [[QLThumbnailGenerationRequest alloc]
            initWithFileAtURL:fileUrl
            size:CGSizeMake(width, height)
            scale:scale
            representationTypes:QLThumbnailGenerationRequestRepresentationTypeThumbnail];

        dispatch_semaphore_t semaphore = dispatch_semaphore_create(0);
        __block NSData *pngData = nil;

        [[QLThumbnailGenerator sharedGenerator]
            generateBestRepresentationForRequest:request
            completionHandler:^(QLThumbnailRepresentation *representation, NSError *error) {
                if (representation != nil && error == nil && representation.CGImage != NULL) {
                    NSBitmapImageRep *bitmap = [[NSBitmapImageRep alloc] initWithCGImage:representation.CGImage];
                    pngData = [bitmap representationUsingType:NSBitmapImageFileTypePNG properties:@{}];
                }

                dispatch_semaphore_signal(semaphore);
            }];

        const int64_t timeoutNanoseconds = 30LL * NSEC_PER_SEC;
        if (dispatch_semaphore_wait(semaphore, dispatch_time(DISPATCH_TIME_NOW, timeoutNanoseconds)) != 0
            || pngData == nil
            || pngData.length == 0) {
            [[QLThumbnailGenerator sharedGenerator] cancelRequest:request];
            return 0;
        }

        unsigned char *buffer = malloc(pngData.length);
        if (buffer == NULL) {
            return 0;
        }

        memcpy(buffer, pngData.bytes, pngData.length);
        *outputBytes = buffer;
        *outputLength = (long)pngData.length;
        return 1;
    }
}

void monkasa_free_buffer(unsigned char *buffer)
{
    free(buffer);
}

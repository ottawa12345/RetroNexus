using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using EmulatorLauncher.Interop;
using EmulatorLauncher.Input;

namespace EmulatorLauncher.Emulation;

/// <summary>EmulationHostのうち、映像描画(HW/SWレンダリング、サムネイル生成、画質スケーラー、ウィンドウ位置/GLコンテキスト再生成)を担当する部分。</summary>
public sealed partial class EmulationHost
{
    // HWレンダリング(OpenGL) 関連ヘルパー

    /// <summary>コアが「今描画すべきフレームバッファ」を問い合わせてくるコールバック本体。</summary>
    private nuint HwGetCurrentFramebuffer() => _hwFbo;

    /// <summary>コアがGL関数ポインタを問い合わせてくるコールバック本体。</summary>
    private nint HwGetProcAddress(string sym) => Sdl.SDL_GL_GetProcAddress(sym);

    /// <summary>HWレンダリング用のFBO(カラーテクスチャ + 必要なら深度/ステンシル)を作成する。</summary>
    private void CreateHwFbo(int width, int height)
    {
        if (width <= 0) width = 640;
        if (height <= 0) height = 480;

        DestroyHwFbo();

        Gl.glGenFramebuffers(1, out _hwFbo);
        Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, _hwFbo);

        Gl.glGenTextures(1, out _hwColorTex);
        Gl.glBindTexture(Gl.GL_TEXTURE_2D, _hwColorTex);
        Gl.glTexImage2D(Gl.GL_TEXTURE_2D, 0, (int)Gl.GL_RGBA8, width, height, 0, Gl.GL_RGBA, Gl.GL_UNSIGNED_BYTE, nint.Zero);
        Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_MIN_FILTER, (int)Gl.GL_LINEAR);
        Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_MAG_FILTER, (int)Gl.GL_LINEAR);
        Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_WRAP_S, (int)Gl.GL_CLAMP_TO_EDGE);
        Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_WRAP_T, (int)Gl.GL_CLAMP_TO_EDGE);
        Gl.glFramebufferTexture2D(Gl.GL_FRAMEBUFFER, Gl.GL_COLOR_ATTACHMENT0, Gl.GL_TEXTURE_2D, _hwColorTex, 0);

        if (_hwDepth || _hwStencil)
        {
            Gl.glGenRenderbuffers(1, out _hwDepthStencilRbo);
            Gl.glBindRenderbuffer(Gl.GL_RENDERBUFFER, _hwDepthStencilRbo);
            uint internalFormat = _hwStencil ? Gl.GL_DEPTH24_STENCIL8 : Gl.GL_DEPTH_COMPONENT24;
            Gl.glRenderbufferStorage(Gl.GL_RENDERBUFFER, internalFormat, width, height);
            Gl.glFramebufferRenderbuffer(Gl.GL_FRAMEBUFFER, Gl.GL_DEPTH_ATTACHMENT, Gl.GL_RENDERBUFFER, _hwDepthStencilRbo);
            if (_hwStencil)
                Gl.glFramebufferRenderbuffer(Gl.GL_FRAMEBUFFER, Gl.GL_STENCIL_ATTACHMENT, Gl.GL_RENDERBUFFER, _hwDepthStencilRbo);
        }

        uint status = Gl.glCheckFramebufferStatus(Gl.GL_FRAMEBUFFER);
        if (status != Gl.GL_FRAMEBUFFER_COMPLETE)
            ReportStatus($"警告: HWレンダリング用FBOが不完全です(status=0x{status:X})");

        Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, 0);

        _hwFboWidth = width;
        _hwFboHeight = height;
    }

    /// <summary>既存のHW-FBO(_hwFbo/_hwColorTex等)を、必要な場合だけ「同じオブジェクトハンドルのまま」より大きなサイズへストレージ拡張する。</summary>
    private void EnsureHwFboCapacity(int minWidth, int minHeight)
    {
        if (minWidth <= 0 || minHeight <= 0) return;

        if (_hwFbo == 0)
        {
            // 通常はSET_HW_RENDER受理時/Start()側で先に作られているはずだが、万一まだ無ければCreateHwFboに任せる。
            CreateHwFbo(minWidth, minHeight);
            return;
        }

        if (minWidth <= _hwFboWidth && minHeight <= _hwFboHeight)
            return; // 既に十分な大きさ

        int newWidth = Math.Max(minWidth, _hwFboWidth);
        int newHeight = Math.Max(minHeight, _hwFboHeight);

        // コアが自前でキャッシュしているFBO/テクスチャ/レンダーバッファの結合状態を壊さないよう退避
        var savedGl = SaveGlState();

        Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, _hwFbo);

        Gl.glBindTexture(Gl.GL_TEXTURE_2D, _hwColorTex);
        Gl.glTexImage2D(Gl.GL_TEXTURE_2D, 0, (int)Gl.GL_RGBA8, newWidth, newHeight, 0, Gl.GL_RGBA, Gl.GL_UNSIGNED_BYTE, nint.Zero);

        if (_hwDepthStencilRbo != 0)
        {
            Gl.glBindRenderbuffer(Gl.GL_RENDERBUFFER, _hwDepthStencilRbo);
            uint internalFormat = _hwStencil ? Gl.GL_DEPTH24_STENCIL8 : Gl.GL_DEPTH_COMPONENT24;
            Gl.glRenderbufferStorage(Gl.GL_RENDERBUFFER, internalFormat, newWidth, newHeight);
        }

        uint status = Gl.glCheckFramebufferStatus(Gl.GL_FRAMEBUFFER);
        if (status != Gl.GL_FRAMEBUFFER_COMPLETE)
            ReportStatus($"警告: HW-FBO拡張後の状態が不完全です(status=0x{status:X})");

        RestoreGlState(savedGl);

        Debug.WriteLine(
            $"[EmulationHost] EnsureHwFboCapacity: HW-FBOを拡張しました "
            + $"{_hwFboWidth}x{_hwFboHeight} => {newWidth}x{newHeight} (ハンドルは維持: fbo={_hwFbo}, tex={_hwColorTex})");

        _hwFboWidth = newWidth;
        _hwFboHeight = newHeight;
    }

    private void DestroyHwFbo()
    {
        if (_hwColorTex != 0) { Gl.glDeleteTextures(1, ref _hwColorTex); _hwColorTex = 0; }
        if (_hwDepthStencilRbo != 0) { Gl.glDeleteRenderbuffers(1, ref _hwDepthStencilRbo); _hwDepthStencilRbo = 0; }
        if (_hwFbo != 0) { Gl.glDeleteFramebuffers(1, ref _hwFbo); _hwFbo = 0; }
        _hwFboWidth = 0;
        _hwFboHeight = 0;
    }


    /// <summary>libretroから毎フレーム呼ばれる描画コールバック。</summary>
    private unsafe void VideoRefreshCallback(nint data, uint width, uint height, nuint pitch)
    {

        if (data == nint.Zero || width == 0 || height == 0)
            return; // 直前フレームの複製(duplicate frame)

        // "resyncGeometryOnFrameSizeChange" アクションが要求されている間は、実際に届くフレームのwidth/heightが基準値(起動時/直近のSET_GEOMETRY時点)から変化するまで毎フレーム監視を続ける。
        if (_geometryResyncPending)
        {
            if ((int)width != _geometryReferenceWidth || (int)height != _geometryReferenceHeight)
            {
                _geometryResyncPending = false;
                _geometryResyncFramesWaited = 0;
                ResyncGeometryFromFrameSize((int)width, (int)height);
            }
            else if (++_geometryResyncFramesWaited > _geometryResyncMaxFramesToWait)
            {
                _geometryResyncPending = false;
                _geometryResyncFramesWaited = 0;
                Debug.WriteLine(
                    $"[EmulationHost] resyncGeometryOnFrameSizeChange: {_geometryResyncMaxFramesToWait}フレーム待っても"
                    + "フレーム形状が変化しなかったため諦めます(コアがこのオプション変更を反映しなかった可能性があります)。");
            }
        }

        bool isHwFrame = data == RetroHwFrameBuffer.Valid;

        if (isHwFrame)
        {
            _lastHwFrameWidth = (int)width;
            _lastHwFrameHeight = (int)height;

            // このフレーム自体は既にコアが(拡張前の)_hwFboへ描画し終えた後なので、ここで拡張しても間に合うのは次のフレームからだが、「気づいた時点ですぐ拡張する」ことで、コマ落ち/崩れが最大でも1フレームで収まるようにする。
            EnsureHwFboCapacity((int)width, (int)height);

            // ステートセーブ画像の撮影中(レイアウトを一時的に変えている間)は、ウィンドウへ表示しない。
            if (_suppressPresent) return;
        }

        // メニュー背景用の縮小サムネイル/巻き戻し用フレームは、CPU側の生ピクセルが手に入るSWレンダリングの時だけ更新できる(HWレンダリング時は別途glReadPixels等での読み戻しが必要になるため、今回は未対応)。
        if (!isHwFrame)
        {
            if (_thumbnailCaptureRequested)
            {
                _thumbnailCaptureRequested = false;
                UpdateThumbnail(data, (int)width, (int)height, (int)pitch);
            }

            if (_captureFullFrameRequested)
            {
                _captureFullFrameRequested = false;
                CaptureFullFrameForRewind(data, (int)width, (int)height, (int)pitch);
            }
        }

        if (_renderer != nint.Zero)
        {
            // SWレンダリングのコア: SDL_Rendererに描画を任せる。
            RenderSwFrameViaSdlRenderer(data, (int)width, (int)height, (int)pitch);
            return;
        }

        int winW = _currentBounds.W;
        int winH = _currentBounds.H;
        if (winW <= 0 || winH <= 0) return;

        // 描画先の大きさは、論理サイズ(_currentBounds)ではなくウィンドウの実ピクセルサイズを使う(DPIスケーリング時に論理サイズとGLの描画先サイズが食い違うため)。
        if (Sdl.SDL_GetWindowSizeInPixels(_sdlWindow, out int pxW, out int pxH) && pxW > 0 && pxH > 0)
        {
            winW = pxW;
            winH = pxH;
        }

        // リサイズ直後の最初の描画フレームで、もう一度コンテキストを付け直してデフォルトフレームバッファのサイズ更新を確実にする。
        if (_needGlSurfaceRefresh)
        {
            _needGlSurfaceRefresh = false;
            Sdl.SDL_GL_MakeCurrent(_sdlWindow, nint.Zero);
            Sdl.SDL_GL_MakeCurrent(_sdlWindow, _glContext);
        }

        if ((int)width != _lastLoggedFrameW || (int)height != _lastLoggedFrameH
            || winW != _lastLoggedWinW || winH != _lastLoggedWinH)
        {
            Debug.WriteLine(
                $"[EmulationHost] VideoRefreshCallback(rawGL): frame={width}x{height} isHwFrame={isHwFrame} "
                + $"=> viewport(_currentBounds)={winW}x{winH}");
            _lastLoggedFrameW = (int)width;
            _lastLoggedFrameH = (int)height;
            _lastLoggedWinW = winW;
            _lastLoggedWinH = winH;
        }

        // HWレンダリングのコアが前提にしているGL状態を退避しておき、画面転送の後に元へ戻す。
        SavedGlState savedGl = default;
        if (isHwFrame) savedGl = SaveGlState();

        Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, 0);
        Gl.glViewport(0, 0, winW, winH);

        if (isHwFrame)
        {
            // glBlitFramebufferはScissorテストの影響を受ける(viewportは無関係)。
            Gl.glDisable(GL_SCISSOR_TEST);
            Gl.glDisable(GL_FRAMEBUFFER_SRGB);

            // コアが既に _hwFbo へ描画済みなので、その内容を画面いっぱいに引き伸ばして転送するだけでよい。
            if (_hwFbo != 0)
            {
                Gl.glBindFramebuffer(Gl.GL_READ_FRAMEBUFFER, _hwFbo);
                Gl.glBindFramebuffer(Gl.GL_DRAW_FRAMEBUFFER, 0);
                // Gl.glBlitFramebuffer(0, 0, (int)width, (int)height, 0, 0, winW, winH, Gl.GL_COLOR_BUFFER_BIT, Gl.GL_LINEAR); bottom_left_origin=falseのコア(DeSmuME等)は上下逆の並びで描くので、転送先のY範囲を逆にして反転する。
                int dstY0 = _hwBottomLeftOrigin ? 0 : winH;
                int dstY1 = _hwBottomLeftOrigin ? winH : 0;
                Gl.glBlitFramebuffer(0, 0, (int)width, (int)height, 0, dstY0, winW, dstY1, Gl.GL_COLOR_BUFFER_BIT, Gl.GL_NEAREST);
                Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, 0);
            }
        }
        else
        {
            UploadSwFrameAndDraw(data, (int)width, (int)height, (int)pitch, winW, winH);
        }

        Sdl.SDL_GL_SwapWindow(_sdlWindow);

        if (isHwFrame) RestoreGlState(savedGl);
    }

    /// <summary>SWレンダリングのコアの生ピクセルバッファをRGB8へ変換してテクスチャへアップロードし、画面いっぱいに引き伸ばしたテクスチャ付き矩形として描画する(固定機能パイプライン)。</summary>
    private unsafe void UploadSwFrameAndDraw(nint data, int width, int height, int pitch, int winW, int winH)
    {
        int bytesPerPixel = _pixelFormat == RetroPixelFormat.XRGB8888 ? 4 : 2;
        int neededLength = width * height * 3;
        if (_swRgbBuffer.Length != neededLength)
            _swRgbBuffer = new byte[neededLength];

        byte* src = (byte*)data;
        fixed (byte* dstBase = _swRgbBuffer)
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = src + (long)y * pitch;
                byte* dstRow = dstBase + (long)y * width * 3;

                for (int x = 0; x < width; x++)
                {
                    DecodePixel(row, x, bytesPerPixel, out byte r, out byte g, out byte b);
                    int di = x * 3;
                    dstRow[di + 0] = r;
                    dstRow[di + 1] = g;
                    dstRow[di + 2] = b;
                }
            }
        }

        // 3バイト/pixelの行は既定の4バイトアライメント前提だとズレて表示されるため、アップロード前に詰め(1バイト境界)を明示しておく。
        Gl.glPixelStorei(Gl.GL_UNPACK_ALIGNMENT, 1);

        if (_swTexture == 0)
        {
            Gl.glGenTextures(1, out _swTexture);
            Gl.glBindTexture(Gl.GL_TEXTURE_2D, _swTexture);
            Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_MIN_FILTER, (int)Gl.GL_LINEAR);
            Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_MAG_FILTER, (int)Gl.GL_LINEAR);
            Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_WRAP_S, (int)Gl.GL_CLAMP_TO_EDGE);
            Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_WRAP_T, (int)Gl.GL_CLAMP_TO_EDGE);
        }
        else
        {
            Gl.glBindTexture(Gl.GL_TEXTURE_2D, _swTexture);
        }

        fixed (byte* rgbPtr = _swRgbBuffer)
        {
            if (width != _swTexW || height != _swTexH)
            {
                Gl.glTexImage2D(Gl.GL_TEXTURE_2D, 0, (int)Gl.GL_RGB, width, height, 0,
                    Gl.GL_RGB, Gl.GL_UNSIGNED_BYTE, (nint)rgbPtr);
                _swTexW = width;
                _swTexH = height;
            }
            else
            {
                Gl.glTexSubImage2D(Gl.GL_TEXTURE_2D, 0, 0, 0, width, height,
                    Gl.GL_RGB, Gl.GL_UNSIGNED_BYTE, (nint)rgbPtr);
            }
        }

        Gl.glDisable(Gl.GL_DEPTH_TEST);
        Gl.glEnable(Gl.GL_TEXTURE_2D);
        Gl.glColor4f(1f, 1f, 1f, 1f);

        Gl.glMatrixMode(Gl.GL_PROJECTION);
        Gl.glLoadIdentity();
        Gl.glOrtho(0, 1, 1, 0, -1, 1); // 左上原点(0,0)〜右下(1,1)の正規化座標

        Gl.glMatrixMode(Gl.GL_MODELVIEW);
        Gl.glLoadIdentity();

        Gl.glClearColor(0f, 0f, 0f, 1f);
        Gl.glClear(Gl.GL_COLOR_BUFFER_BIT);

        // 画面(0,0)-(1,1)いっぱいに引き伸ばして描画する(レターボックス無し、常時フルストレッチ)。
        Gl.glBegin(Gl.GL_QUADS);
        Gl.glTexCoord2f(0f, 0f); Gl.glVertex2f(0f, 0f);
        Gl.glTexCoord2f(1f, 0f); Gl.glVertex2f(1f, 0f);
        Gl.glTexCoord2f(1f, 1f); Gl.glVertex2f(1f, 1f);
        Gl.glTexCoord2f(0f, 1f); Gl.glVertex2f(0f, 1f);
        Gl.glEnd();

        Gl.glDisable(Gl.GL_TEXTURE_2D);
    }

    /// <summary>libretroから渡された生のフレームバッファ(RGB565/XRGB1555/XRGB8888)を、メニュー背景用の小さなBGRA8サムネイルへ最近傍サンプリングで縮小し、簡易ボックスブラーをかけて _thumbBgra に書き戻す。</summary>
    private unsafe void UpdateThumbnail(nint data, int width, int height, int pitch)
    {
        if (width <= 0 || height <= 0 || pitch <= 0) return;

        // 元映像のアスペクト比を保ったまま、長辺が ThumbnailMaxDim に収まるサイズを求める。
        int thumbW, thumbH;
        if (width >= height)
        {
            thumbW = ThumbnailMaxDim;
            thumbH = Math.Max(1, (int)Math.Round(ThumbnailMaxDim * (double)height / width));
        }
        else
        {
            thumbH = ThumbnailMaxDim;
            thumbW = Math.Max(1, (int)Math.Round(ThumbnailMaxDim * (double)width / height));
        }

        int neededLength = thumbW * thumbH * 4;
        if (_thumbWorkBuf.Length != neededLength)
            _thumbWorkBuf = new byte[neededLength];

        byte[] localBuf = _thumbWorkBuf;

        int bytesPerPixel = _pixelFormat == RetroPixelFormat.XRGB8888 ? 4 : 2;
        byte* src = (byte*)data;

        for (int ty = 0; ty < thumbH; ty++)
        {
            int sy = ty * height / thumbH;
            if (sy >= height) sy = height - 1;
            byte* row = src + (long)sy * pitch;

            for (int tx = 0; tx < thumbW; tx++)
            {
                int sx = tx * width / thumbW;
                if (sx >= width) sx = width - 1;

                DecodePixel(row, sx, bytesPerPixel, out byte r, out byte g, out byte b);

                int di = (ty * thumbW + tx) * 4;
                localBuf[di + 0] = b;
                localBuf[di + 1] = g;
                localBuf[di + 2] = r;
                localBuf[di + 3] = 255;
            }
        }

        // SET_ROTATIONで90/270度回転が申告されているコア向け。
        if (_coreRotationQuarterTurns != 0)
        {
            localBuf = RotateBgraBuffer(localBuf, thumbW, thumbH, _coreRotationQuarterTurns, ref _thumbRotateBuf, out int rotatedW, out int rotatedH);
            thumbW = rotatedW;
            thumbH = rotatedH;
        }

        // ステートセーブのスクリーンショット用に、ブラーをかける前の状態を控えておく。
        if (_rawThumbBgra.Length != neededLength)
            _rawThumbBgra = new byte[neededLength];
        Buffer.BlockCopy(localBuf, 0, _rawThumbBgra, 0, neededLength);
        _rawThumbWidth = thumbW;
        _rawThumbHeight = thumbH;
        _rawThumbValid = true;

        // 見た目をSwitch風の柔らかいぼかしに近づけるため、軽いボックスブラーを数回かける。
        BoxBlur3X3(localBuf, thumbW, thumbH, passes: 2);

        lock (_thumbLock)
        {
            if (_thumbBgra.Length != neededLength)
                _thumbBgra = new byte[neededLength];

            Buffer.BlockCopy(localBuf, 0, _thumbBgra, 0, neededLength);
            _thumbWidth = thumbW;
            _thumbHeight = thumbH;
            _thumbValid = true;
        }
    }

    private static void ComputeThumbnailSize(int width, int height, out int thumbW, out int thumbH)
    {
        if (width >= height)
        {
            thumbW = ThumbnailMaxDim;
            thumbH = Math.Max(1, (int)Math.Round(ThumbnailMaxDim * (double)height / width));
        }
        else
        {
            thumbH = ThumbnailMaxDim;
            thumbW = Math.Max(1, (int)Math.Round(ThumbnailMaxDim * (double)width / height));
        }
    }

    /// <summary>縮小済み・ブラー未適用のtop-down BGRA8サムネイルを受け取り、ステートセーブ用の生画像(_rawThumbBgra)とメニュー背景用のぼかし画像(_thumbBgra)を更新する。</summary>
    private void PublishThumbnail(byte[] topDownBgra, int thumbW, int thumbH)
    {
        // SET_ROTATIONで90/270度回転が申告されているコア向け(UpdateThumbnail側と同じ理由)。
        if (_coreRotationQuarterTurns != 0)
        {
            topDownBgra = RotateBgraBuffer(topDownBgra, thumbW, thumbH, _coreRotationQuarterTurns, ref _publishThumbRotateBuf, out int rotatedW, out int rotatedH);
            thumbW = rotatedW;
            thumbH = rotatedH;
        }

        int neededLength = thumbW * thumbH * 4;

        if (_rawThumbBgra.Length != neededLength)
            _rawThumbBgra = new byte[neededLength];
        Buffer.BlockCopy(topDownBgra, 0, _rawThumbBgra, 0, neededLength);
        _rawThumbWidth = thumbW;
        _rawThumbHeight = thumbH;
        _rawThumbValid = true;

        BoxBlur3X3(topDownBgra, thumbW, thumbH, passes: 2);

        lock (_thumbLock)
        {
            if (_thumbBgra.Length != neededLength)
                _thumbBgra = new byte[neededLength];
            Buffer.BlockCopy(topDownBgra, 0, _thumbBgra, 0, neededLength);
            _thumbWidth = thumbW;
            _thumbHeight = thumbH;
            _thumbValid = true;
        }
    }

    private void EnsureHwThumbFbo(int width, int height)
    {
        if (_hwThumbFbo != 0 && _hwThumbFboWidth == width && _hwThumbFboHeight == height)
            return;

        DestroyHwThumbFbo();

        Gl.glGenFramebuffers(1, out _hwThumbFbo);
        Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, _hwThumbFbo);

        Gl.glGenTextures(1, out _hwThumbTex);
        Gl.glBindTexture(Gl.GL_TEXTURE_2D, _hwThumbTex);
        Gl.glTexImage2D(Gl.GL_TEXTURE_2D, 0, (int)Gl.GL_RGBA8, width, height, 0, Gl.GL_RGBA, Gl.GL_UNSIGNED_BYTE, nint.Zero);
        Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_MIN_FILTER, (int)Gl.GL_LINEAR);
        Gl.glTexParameteri(Gl.GL_TEXTURE_2D, Gl.GL_TEXTURE_MAG_FILTER, (int)Gl.GL_LINEAR);
        Gl.glFramebufferTexture2D(Gl.GL_FRAMEBUFFER, Gl.GL_COLOR_ATTACHMENT0, Gl.GL_TEXTURE_2D, _hwThumbTex, 0);

        Gl.glBindFramebuffer(Gl.GL_FRAMEBUFFER, 0);

        _hwThumbFboWidth = width;
        _hwThumbFboHeight = height;
    }

    private void DestroyHwThumbFbo()
    {
        if (_hwThumbTex != 0) { Gl.glDeleteTextures(1, ref _hwThumbTex); _hwThumbTex = 0; }
        if (_hwThumbFbo != 0) { Gl.glDeleteFramebuffers(1, ref _hwThumbFbo); _hwThumbFbo = 0; }
        _hwThumbFboWidth = 0;
        _hwThumbFboHeight = 0;
    }

    private unsafe void CaptureHwThumbnail(int frameWidth, int frameHeight, bool stateOnly = false)
    {
        if (_hwFbo == 0 || frameWidth <= 0 || frameHeight <= 0) return;

        // フロントエンド側のGL操作の前後でコア(Dolphin等)が前提にしているGL状態を壊さない。
        var savedGl = SaveGlState();
        GlGetIntegerv(GL_PACK_ALIGNMENT, out int savedPackAlignment);
        Gl.glDisable(GL_SCISSOR_TEST);
        Gl.glDisable(GL_FRAMEBUFFER_SRGB);

        ComputeThumbnailSize(frameWidth, frameHeight, out int thumbW, out int thumbH);
        EnsureHwThumbFbo(thumbW, thumbH);

        Gl.glBindFramebuffer(Gl.GL_READ_FRAMEBUFFER, _hwFbo);
        Gl.glBindFramebuffer(Gl.GL_DRAW_FRAMEBUFFER, _hwThumbFbo);
        // 画面と同じく、上下逆の並びで描くコアはここで反転して「左下起点」の並びにそろえる(後段の読み出しは左下起点前提)。
        Gl.glBlitFramebuffer(0, 0, frameWidth, frameHeight, 0, _hwBottomLeftOrigin ? 0 : thumbH, thumbW, _hwBottomLeftOrigin ? thumbH : 0,
            Gl.GL_COLOR_BUFFER_BIT, Gl.GL_LINEAR);

        Gl.glBindFramebuffer(Gl.GL_READ_FRAMEBUFFER, _hwThumbFbo);
        Gl.glBindFramebuffer(Gl.GL_DRAW_FRAMEBUFFER, 0);

        int neededLength = thumbW * thumbH * 4;
        if (_hwThumbReadBuf.Length != neededLength)
            _hwThumbReadBuf = new byte[neededLength];

        // citra / melonDS のようにPBO(ピクセルバッファ)を使うコアは、GL_PIXEL_PACK_BUFFERを結合したままにしていることがある。
        GlGetIntegerv(Gl.GL_PIXEL_PACK_BUFFER_BINDING, out int savedPackBuffer);
        if (savedPackBuffer != 0) Gl.glBindBuffer(Gl.GL_PIXEL_PACK_BUFFER, 0);
        Gl.glReadBuffer(Gl.GL_COLOR_ATTACHMENT0);

        Gl.glPixelStorei(Gl.GL_PACK_ALIGNMENT, 1);
        fixed (byte* readPtr = _hwThumbReadBuf)
        {
            Gl.glReadPixels(0, 0, thumbW, thumbH, Gl.GL_BGRA, Gl.GL_UNSIGNED_BYTE, (nint)readPtr);
        }

        Gl.glPixelStorei(Gl.GL_PACK_ALIGNMENT, savedPackAlignment);
        if (savedPackBuffer != 0) Gl.glBindBuffer(Gl.GL_PIXEL_PACK_BUFFER, (uint)savedPackBuffer);
        RestoreGlState(savedGl);

        // glReadPixelsは(0,0)=左下起点で返ってくるため、SW側の並び(top-down)に合わせて上下反転しながらコピーする。
        if (_thumbWorkBuf.Length != neededLength)
            _thumbWorkBuf = new byte[neededLength];

        int rowBytes = thumbW * 4;
        for (int y = 0; y < thumbH; y++)
        {
            int srcRow = thumbH - 1 - y;
            Buffer.BlockCopy(_hwThumbReadBuf, srcRow * rowBytes, _thumbWorkBuf, y * rowBytes, rowBytes);
        }

        // 読み戻したサムネイルが真っ黒かどうかを数える(アルファは無視したRGBの非ゼロ画素数。0なら読み出し自体が失敗している)。
        int nonBlack = 0;
        for (int i = 0; i < neededLength; i += 4)
        {
            if ((_thumbWorkBuf[i] | _thumbWorkBuf[i + 1] | _thumbWorkBuf[i + 2]) != 0) nonBlack++;
            _thumbWorkBuf[i + 3] = 0xFF; // メニュー背景用に不透明へそろえる(コアによってはアルファ0で描かれる)
        }

        if (stateOnly)
        {
            // 上画面のみへ切り替えた結果が真っ黒なら(コアがレイアウト変更を反映できなかった等)、採用せず通常の全体画像へフォールバックする(画像が消えるよりは全体画像の方が良い)。
            if (nonBlack == 0)
            {
                _stateThumbValid = false;
                return;
            }

            // ステートセーブ専用(メニュー背景は更新しない)。
            int outW = thumbW, outH = thumbH;
            if (thumbW * 3 > thumbH * 4) outH = (thumbW * 3 + 3) / 4;
            else if (thumbW * 3 < thumbH * 4) outW = (thumbH * 4 + 2) / 3;

            int outLength = outW * outH * 4;
            if (_stateThumbBgra.Length != outLength)
                _stateThumbBgra = new byte[outLength];

            if (outW == thumbW && outH == thumbH)
            {
                Buffer.BlockCopy(_thumbWorkBuf, 0, _stateThumbBgra, 0, neededLength);
            }
            else
            {
                // 黒(不透明)で塗りつぶし、中央へ配置する
                for (int i = 0; i < outLength; i += 4)
                {
                    _stateThumbBgra[i] = 0; _stateThumbBgra[i + 1] = 0; _stateThumbBgra[i + 2] = 0; _stateThumbBgra[i + 3] = 0xFF;
                }
                int offX = (outW - thumbW) / 2, offY = (outH - thumbH) / 2;
                for (int y = 0; y < thumbH; y++)
                    Buffer.BlockCopy(_thumbWorkBuf, y * thumbW * 4, _stateThumbBgra, ((y + offY) * outW + offX) * 4, thumbW * 4);
            }
            _stateThumbW = outW;
            _stateThumbH = outH;
            _stateThumbValid = true;
            return;
        }

        PublishThumbnail(_thumbWorkBuf, thumbW, thumbH);
    }

    /// <summary>libretroから渡された生のフレームバッファを、縮小・ブラーなしのオリジナル解像度BGRA8へ変換して _fullFrameBgra に保持する。</summary>
    private unsafe void CaptureFullFrameForRewind(nint data, int width, int height, int pitch)
    {
        if (width <= 0 || height <= 0 || pitch <= 0) return;

        int neededLength = width * height * 4;
        if (_fullFrameBgra.Length != neededLength)
            _fullFrameBgra = new byte[neededLength];

        int bytesPerPixel = _pixelFormat == RetroPixelFormat.XRGB8888 ? 4 : 2;
        byte* src = (byte*)data;

        fixed (byte* dstBase = _fullFrameBgra)
        {
            for (int y = 0; y < height; y++)
            {
                byte* row = src + (long)y * pitch;
                byte* dstRow = dstBase + (long)y * width * 4;

                for (int x = 0; x < width; x++)
                {
                    DecodePixel(row, x, bytesPerPixel, out byte r, out byte g, out byte b);

                    int di = x * 4;
                    dstRow[di + 0] = b;
                    dstRow[di + 1] = g;
                    dstRow[di + 2] = r;
                    dstRow[di + 3] = 255;
                }
            }
        }

        // SET_ROTATIONで90/270度回転が申告されているコア向け(UpdateThumbnail側と同じ理由。巻き戻しのフィルムストリップ用スクリーンショットもここで独立に回転させておかないと、ゲーム画面本体だけ正しい向きで、巻き戻し一覧のサムネイルは回転前の向きのままになる)。
        if (_coreRotationQuarterTurns != 0)
        {
            // 注意: ここは _fullFrameBgra 自身を毎回書き換えて次回の入力にもしているため、UpdateThumbnail/PublishThumbnail側のような「呼び出し元専用の使い回しバッファ」を固定で割り当てると、2回目以降の呼び出しで src と reuseBuffer が同一配列を指してしまい(1回目の出力=_fullFrameBgraが2回目の入力にもなるため)、回転処理が自分自身を読みながら上書きして画像が化ける。
            byte[] rotateScratch = Array.Empty<byte>();
            _fullFrameBgra = RotateBgraBuffer(_fullFrameBgra, width, height, _coreRotationQuarterTurns, ref rotateScratch, out int rotatedW, out int rotatedH);
            width = rotatedW;
            height = rotatedH;
        }

        _fullFrameWidth = width;
        _fullFrameHeight = height;
        _fullFrameValid = true;
    }

    /// <summary>1ピクセル分をコアのピクセルフォーマットに応じてRGB8へデコードする。</summary>
    private unsafe void DecodePixel(byte* row, int x, int bytesPerPixel, out byte r, out byte g, out byte b)
    {
        if (bytesPerPixel == 4)
        {
            // XRGB8888 (SDL_PIXELFORMAT_ARGB8888, リトルエンディアン): メモリ順は B,G,R,X
            byte* p = row + (long)x * 4;
            b = p[0];
            g = p[1];
            r = p[2];
            return;
        }

        // 16bit: RGB565 または 0RGB1555(既定)
        byte* p16 = row + (long)x * 2;
        int v = p16[0] | (p16[1] << 8);

        if (_pixelFormat == RetroPixelFormat.RGB565)
        {
            int r5 = (v >> 11) & 0x1F;
            int g6 = (v >> 5) & 0x3F;
            int b5 = v & 0x1F;
            r = (byte)((r5 * 255 + 15) / 31);
            g = (byte)((g6 * 255 + 31) / 63);
            b = (byte)((b5 * 255 + 15) / 31);
        }
        else
        {
            // 0RGB1555
            int r5 = (v >> 10) & 0x1F;
            int g5 = (v >> 5) & 0x1F;
            int b5 = v & 0x1F;
            r = (byte)((r5 * 255 + 15) / 31);
            g = (byte)((g5 * 255 + 15) / 31);
            b = (byte)((b5 * 255 + 15) / 31);
        }
    }

    /// <summary>top-downのBGRA8バッファ(既に縮小・変換済みのもの)を、SET_ROTATIONで申告された回転量(rotationQuarters。libretroの定義通り0〜3、反時計回り基準)に従って実際に回転させた新しいバッファを返す。</summary>
    private static byte[] RotateBgraBuffer(byte[] src, int srcW, int srcH, uint rotationQuarters, ref byte[] reuseBuffer, out int dstW, out int dstH)
    {
        rotationQuarters &= 3;
        if (rotationQuarters == 0 || srcW <= 0 || srcH <= 0)
        {
            dstW = srcW;
            dstH = srcH;
            return src;
        }

        if (reuseBuffer.Length != src.Length)
            reuseBuffer = new byte[src.Length];

        if (rotationQuarters == 2)
        {
            // 180度: 幅・高さは変わらず、点対称に並べ替えるだけ。
            dstW = srcW;
            dstH = srcH;
            var dst180 = reuseBuffer;
            int rowBytes = srcW * 4;
            for (int y = 0; y < srcH; y++)
            {
                int srcRowOffset = y * rowBytes;
                int dstRowOffset = (srcH - 1 - y) * rowBytes;
                for (int x = 0; x < srcW; x++)
                {
                    int si = srcRowOffset + x * 4;
                    int di = dstRowOffset + (srcW - 1 - x) * 4;
                    dst180[di + 0] = src[si + 0];
                    dst180[di + 1] = src[si + 1];
                    dst180[di + 2] = src[si + 2];
                    dst180[di + 3] = src[si + 3];
                }
            }

            return dst180;
        }

        // 90/270度: 幅と高さが入れ替わる。
        dstW = srcH;
        dstH = srcW;
        var dst = reuseBuffer;
        bool clockwise = rotationQuarters == 3;

        for (int dy = 0; dy < dstH; dy++)
        {
            for (int dx = 0; dx < dstW; dx++)
            {
                int sx, sy;
                if (clockwise)
                {
                    sx = dy;
                    sy = srcH - 1 - dx;
                }
                else
                {
                    sx = srcW - 1 - dy;
                    sy = dx;
                }

                int si = (sy * srcW + sx) * 4;
                int di = (dy * dstW + dx) * 4;
                dst[di + 0] = src[si + 0];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }

        return dst;
    }

    /// <summary>BGRA8バッファに対する簡易な3x3ボックスブラー(境界はクランプ)。</summary>
    private void BoxBlur3X3(byte[] pixels, int width, int height, int passes)
    {
        // 以前はここで毎フレーム(呼び出しごと) new byte[pixels.Length] していたが、これがエミュレーションスレッド(retro_run()呼び出しと音声フレームタイミング制御を兼ねる)上での継続的なGen0 GC圧の主因になっていた。
        if (_blurTempBuf.Length != pixels.Length)
            _blurTempBuf = new byte[pixels.Length];
        var temp = _blurTempBuf;

        for (int pass = 0; pass < passes; pass++)
        {
            for (int y = 0; y < height; y++)
            {
                int y0 = Math.Max(0, y - 1);
                int y1 = Math.Min(height - 1, y + 1);

                for (int x = 0; x < width; x++)
                {
                    int x0 = Math.Max(0, x - 1);
                    int x1 = Math.Min(width - 1, x + 1);

                    int bSum = 0, gSum = 0, rSum = 0, count = 0;
                    for (int sy = y0; sy <= y1; sy++)
                    {
                        int rowBase = sy * width;
                        for (int sx = x0; sx <= x1; sx++)
                        {
                            int si = (rowBase + sx) * 4;
                            bSum += pixels[si + 0];
                            gSum += pixels[si + 1];
                            rSum += pixels[si + 2];
                            count++;
                        }
                    }

                    int di = (y * width + x) * 4;
                    temp[di + 0] = (byte)(bSum / count);
                    temp[di + 1] = (byte)(gSum / count);
                    temp[di + 2] = (byte)(rSum / count);
                    temp[di + 3] = 255;
                }
            }

            Buffer.BlockCopy(temp, 0, pixels, 0, pixels.Length);
        }
    }

    // リサイズ直後にGLコンテキストを付け直す必要があるか(ApplyPendingBoundsIfAny / VideoRefreshCallback参照)。
    private bool _needGlSurfaceRefresh;

    private const uint GL_SCISSOR_TEST = 0x0C11;
    private const uint GL_FRAMEBUFFER_SRGB = 0x8DB9;
    private const uint GL_VIEWPORT = 0x0BA2;
    private const uint GL_PACK_ALIGNMENT = 0x0D05;
    private const uint GL_TEXTURE_BINDING_2D = 0x8069;
    private const uint GL_RENDERBUFFER_BINDING = 0x8CA7;
    private const uint GL_DRAW_FRAMEBUFFER_BINDING = 0x8CA6;
    private const uint GL_READ_FRAMEBUFFER_BINDING = 0x8CAA;

    [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv", CallingConvention = CallingConvention.StdCall)]
    private static extern void GlGetIntegerv(uint pname, out int value);

    [DllImport("opengl32.dll", EntryPoint = "glGetIntegerv", CallingConvention = CallingConvention.StdCall)]
    private static extern unsafe void GlGetIntegerv4(uint pname, int* values);

    [DllImport("opengl32.dll", EntryPoint = "glIsEnabled", CallingConvention = CallingConvention.StdCall)]
    [return: MarshalAs(UnmanagedType.I1)]
    private static extern bool GlIsEnabled(uint cap);

    /// <summary>フロントエンド側のGL操作の前後で、コア(HWレンダリング)が前提にしているGL状態を壊さないための退避データ。</summary>
    private struct SavedGlState
    {
        public int DrawFbo, ReadFbo, Tex2D, Rbo;
        public int VpX, VpY, VpW, VpH;
        public bool Scissor, Srgb;
    }

    private static unsafe SavedGlState SaveGlState()
    {
        var st = new SavedGlState();
        GlGetIntegerv(GL_DRAW_FRAMEBUFFER_BINDING, out st.DrawFbo);
        GlGetIntegerv(GL_READ_FRAMEBUFFER_BINDING, out st.ReadFbo);
        GlGetIntegerv(GL_TEXTURE_BINDING_2D, out st.Tex2D);
        GlGetIntegerv(GL_RENDERBUFFER_BINDING, out st.Rbo);
        int* vp = stackalloc int[4];
        GlGetIntegerv4(GL_VIEWPORT, vp);
        st.VpX = vp[0]; st.VpY = vp[1]; st.VpW = vp[2]; st.VpH = vp[3];
        st.Scissor = GlIsEnabled(GL_SCISSOR_TEST);
        st.Srgb = GlIsEnabled(GL_FRAMEBUFFER_SRGB);
        return st;
    }

    private static void RestoreGlState(in SavedGlState st)
    {
        Gl.glBindFramebuffer(Gl.GL_DRAW_FRAMEBUFFER, (uint)st.DrawFbo);
        Gl.glBindFramebuffer(Gl.GL_READ_FRAMEBUFFER, (uint)st.ReadFbo);
        Gl.glBindTexture(Gl.GL_TEXTURE_2D, (uint)st.Tex2D);
        Gl.glBindRenderbuffer(Gl.GL_RENDERBUFFER, (uint)st.Rbo);
        Gl.glViewport(st.VpX, st.VpY, st.VpW, st.VpH);
        if (st.Scissor) Gl.glEnable(GL_SCISSOR_TEST); else Gl.glDisable(GL_SCISSOR_TEST);
        if (st.Srgb) Gl.glEnable(GL_FRAMEBUFFER_SRGB); else Gl.glDisable(GL_FRAMEBUFFER_SRGB);
    }

    /// <summary>XAML側のプレースホルダに追従させて、描画ウィンドウの位置・サイズ・Zオーダーを更新する。</summary>
    private void ApplyPendingBoundsIfAny(bool forceZOrder, double now)
    {
        if (_sdlWindow == nint.Zero)
            return;

        bool changed = false;
        if (_boundsDirty)
        {
            lock (_boundsLock)
            {
                _currentBounds = _pendingBounds;
                _boundsDirty = false;
            }
            changed = true;
        }

        if (_currentBounds.W <= 0 || _currentBounds.H <= 0)
            return;

        if (changed)
        {

            // ネイティブウィンドウ(_sdlWindow、SDLが自前で作成・所有している)の位置・サイズを更新する。
            Sdl.SDL_SetWindowPosition(_sdlWindow, _currentBounds.X, _currentBounds.Y);
            Sdl.SDL_SetWindowSize(_sdlWindow, _currentBounds.W, _currentBounds.H);

            // raw GL(HWレンダリング)では、リサイズ後もドライバ(特にAMD)がWGLのデフォルトフレームバッファのサイズを古いまま保持し続けることがある。
            if (_renderer == nint.Zero && _glContext != nint.Zero)
            {
                Sdl.SDL_SyncWindow(_sdlWindow);
                Sdl.SDL_GL_MakeCurrent(_sdlWindow, nint.Zero);
                Sdl.SDL_GL_MakeCurrent(_sdlWindow, _glContext);
                _needGlSurfaceRefresh = true;
            }

            // SWレンダリングのコア(SDL_Renderer使用中)は、論理解像度(コアの映像出力解像度)を改めて設定し直しておく。
            EnsureSdlLogicalPresentation();
        }

        if (changed || forceZOrder)
        {
            // リサイズ/移動のたびに毎回前面へ上げ直しておく(forceZOrderによる250ms毎の再アサートだけに任せず、座標が変わった直後にも必ず行う)。
            if (_childHwnd != nint.Zero)
            {
                Win32.SetWindowPos(
                    _childHwnd, Win32.HWND_TOP, 0, 0, 0, 0,
                    Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE | Win32.SWP_SHOWWINDOW);
            }
        }
    }

    /// <summary>SWレンダリングのコア用に、raw GLコンテキストを破棄してSDL_Rendererへ切り替える。</summary>
    private void SwitchToSdlRendererForSwRendering()
    {
        if (_glContext != nint.Zero)
        {
            Sdl.SDL_GL_MakeCurrent(_sdlWindow, nint.Zero);
            Sdl.SDL_GL_DestroyContext(_glContext);
            _glContext = nint.Zero;
        }

        _renderer = Sdl.SDL_CreateRenderer(_sdlWindow, null);
        if (_renderer == nint.Zero)
        {
            ReportStatus("SDL_CreateRenderer に失敗しました: " + Sdl.SDL_GetErrorString());
            return;
        }

        Sdl.SDL_SetRenderVSync(_renderer, 0);
    }

    /// <summary>SWレンダリングのコアの生ピクセルバッファ(RGB565/XRGB1555/XRGB8888)をそのままSDLのストリーミングテクスチャへアップロードし、SDL_Rendererで描画する。</summary>
    private void EnsureSdlLogicalPresentation()
    {
        if (_renderer == nint.Zero || _sdlTexW == 0 || _sdlTexH == 0)
            return;

        uint rotationQuarters = _coreRotationQuarterTurns & 3;
        bool swapForRotation = rotationQuarters == 1 || rotationQuarters == 3;
        int logicalW = swapForRotation ? (int)_sdlTexH : (int)_sdlTexW;
        int logicalH = swapForRotation ? (int)_sdlTexW : (int)_sdlTexH;

        if (logicalW == _sdlLogicalW && logicalH == _sdlLogicalH)
            return;

        Sdl.SDL_SetRenderLogicalPresentation(_renderer, logicalW, logicalH,
            Sdl.SDL_LOGICAL_PRESENTATION_STRETCH);
        _sdlLogicalW = logicalW;
        _sdlLogicalH = logicalH;
    }

    private unsafe void RenderSwFrameViaSdlRenderer(nint data, int width, int height, int pitch)
    {
        if (width != _lastLoggedFrameW || height != _lastLoggedFrameH
            || _currentBounds.W != _lastLoggedWinW || _currentBounds.H != _lastLoggedWinH)
        {
            Debug.WriteLine(
                $"[EmulationHost] RenderSwFrameViaSdlRenderer: frame={width}x{height} "
                + $"=> _currentBounds(SDLウィンドウサイズ)={_currentBounds.W}x{_currentBounds.H}");
            _lastLoggedFrameW = width;
            _lastLoggedFrameH = height;
            _lastLoggedWinW = _currentBounds.W;
            _lastLoggedWinH = _currentBounds.H;
        }

        int bpp = _pixelFormat == RetroPixelFormat.XRGB8888 ? 4 : 2;
        int scaleFactor = GetPictureQualityScaleFactor(_pictureQuality);

        // アップロードするデータ/サイズ/ピッチ。
        nint uploadData = data;
        int uploadPitch = pitch;
        int uploadWidth = width;
        int uploadHeight = height;

        if (scaleFactor > 1)
        {
            EnsureHqScaleBuffer(width, height, bpp, scaleFactor);

            switch (_pictureQuality)
            {
                case PictureQualityMode.HighQuality3x:
                    ApplyHighQuality3xScale((byte*)data, pitch, width, height, bpp, _hqScaleBuffer);
                    break;
                case PictureQualityMode.Hq2x:
                    ApplyHq2xScale((byte*)data, pitch, width, height, bpp, _hqScaleBuffer);
                    break;
            }

            uploadWidth = width * scaleFactor;
            uploadHeight = height * scaleFactor;
            uploadPitch = uploadWidth * bpp;
        }

        if (_sdlTexture == nint.Zero || uploadWidth != _sdlTexW || uploadHeight != _sdlTexH)
        {
            if (_sdlTexture != nint.Zero) Sdl.SDL_DestroyTexture(_sdlTexture);

            uint sdlFormat = _pixelFormat switch
            {
                RetroPixelFormat.RGB565 => Sdl.SDL_PIXELFORMAT_RGB565,
                RetroPixelFormat.XRGB8888 => Sdl.SDL_PIXELFORMAT_ARGB8888,
                _ => Sdl.SDL_PIXELFORMAT_XRGB1555,
            };

            _sdlTexture = Sdl.SDL_CreateTexture(_renderer, sdlFormat,
                Sdl.SDL_TEXTUREACCESS_STREAMING, uploadWidth, uploadHeight);
            _sdlTexW = (uint)uploadWidth;
            _sdlTexH = (uint)uploadHeight;

            Sdl.SDL_SetTextureBlendMode(_sdlTexture, Sdl.SDL_BLENDMODE_NONE);
            // どちらのスケーラーも「なし」時と同じくNEAREST出力にしておく。
            Sdl.SDL_SetTextureScaleMode(_sdlTexture, Sdl.SDL_SCALEMODE_NEAREST);
        }

        if (scaleFactor > 1)
        {
            fixed (byte* scaledPtr = _hqScaleBuffer)
                Sdl.SDL_UpdateTexture(_sdlTexture, nint.Zero, (nint)scaledPtr, uploadPitch);
        }
        else
        {
            Sdl.SDL_UpdateTexture(_sdlTexture, nint.Zero, uploadData, uploadPitch);
        }

        // SET_ROTATIONで90/270度回転が申告されているコア(アーケード系の縦画面(TATE)ゲーム等)向け。
        EnsureSdlLogicalPresentation();

        uint rotationQuarters = _coreRotationQuarterTurns & 3;

        Sdl.SDL_RenderClear(_renderer);

        if (rotationQuarters == 0)
        {
            Sdl.SDL_RenderTexture(_renderer, _sdlTexture, nint.Zero, nint.Zero);
        }
        else
        {
            int logicalW = _sdlLogicalW;
            int logicalH = _sdlLogicalH;
            // libretro(RETRO_ENVIRONMENT_SET_ROTATION)は反時計回りの度数だが、SDL_RenderTextureRotatedのangleは時計回りのため符号を変換する。
            double angle = rotationQuarters switch
            {
                1 => 270.0, // 反時計回り90度 = 時計回り270度
                2 => 180.0,
                3 => 90.0,  // 反時計回り270度 = 時計回り90度
                _ => 0.0,
            };

            // dstrectはテクスチャの「回転前の」向き・サイズのまま、論理解像度(回転後の向き)の中央に配置する。
            var dst = new Sdl.SDL_FRect
            {
                X = (logicalW - uploadWidth) / 2f,
                Y = (logicalH - uploadHeight) / 2f,
                W = uploadWidth,
                H = uploadHeight,
            };
            var center = new Sdl.SDL_FPoint { X = uploadWidth / 2f, Y = uploadHeight / 2f };

            Sdl.SDL_RenderTextureRotated(_renderer, _sdlTexture, nint.Zero, ref dst, angle, ref center, Sdl.SDL_FLIP_NONE);
        }

        // 早送り中(倍率!=1)だけ、画面右上に現在の倍率を数字で表示する。
        DrawFastForwardIndicatorIfActive(_sdlLogicalW, _sdlLogicalH);

        Sdl.SDL_RenderPresent(_renderer);
    }

    // 0〜9の7セグメント表現(a,b,c,d,e,f,gの順。セグメント名は一般的なアルファベット割り当てに準拠: a=上, b=右上, c=右下, d=下, e=左下, f=左上, g=中央)。
    private static readonly bool[][] SevenSegmentTable =
    {
        new[] { true,  true,  true,  true,  true,  true,  false }, // 0
        new[] { false, true,  true,  false, false, false, false }, // 1
        new[] { true,  true,  false, true,  true,  false, true  }, // 2
        new[] { true,  true,  true,  true,  false, false, true  }, // 3
        new[] { false, true,  true,  false, false, true,  true  }, // 4
        new[] { true,  false, true,  true,  false, true,  true  }, // 5
        new[] { true,  false, true,  true,  true,  true,  true  }, // 6
        new[] { true,  true,  true,  false, false, false, false }, // 7
        new[] { true,  true,  true,  true,  true,  true,  true  }, // 8
        new[] { true,  true,  true,  true,  false, true,  true  }, // 9
    };

    // 「×」記号を5x5のドット絵で表現する定義(掛け算・倍率であることを示すため、数字の前に添える)。
    private static readonly bool[,] MultiplySymbolGlyph =
    {
        { true,  false, false, false, true  },
        { false, true,  false, true,  false },
        { false, false, true,  false, false },
        { false, true,  false, true,  false },
        { true,  false, false, false, true  },
    };

    /// <summary>現在の早送り倍率()が1(通常速度)以外の間だけ、SDL_Rendererの論理座標系(logicalW x logicalH。EnsureSdlLogicalPresentation参照)の右上に、半透明の背景付きで「×倍率」(例: ×2)を小さく描画する。</summary>
    private void DrawFastForwardIndicatorIfActive(int logicalW, int logicalH)
    {
        int multiplier = FastForwardMultiplier;
        if (multiplier == 1) return;

        // FastForwardMultipliers = {1,2,3,5} なので常に1桁の数字で足りる。
        int digit = multiplier;

        // 以前(digit単体で44x56)の約半分のサイズ感。
        const float margin = 8f;       // 画面端からのマージン
        const float pad = 4f;          // 背景ボックス内側の余白
        const float symbolW = 11f, symbolH = 15f; // 「×」記号の描画領域
        const float gap = 3f;          // 「×」と数字の間隔
        const float digitW = 12f, digitH = 15f;   // 数字の描画領域
        const float thickness = 2f;    // セグメント/ドットの線幅相当

        float boxW = pad * 2f + symbolW + gap + digitW;
        float boxH = pad * 2f + digitH;
        float boxX = logicalW - boxW - margin;
        float boxY = margin;

        Sdl.SDL_SetRenderDrawBlendMode(_renderer, Sdl.SDL_BLENDMODE_BLEND);

        // 背景(半透明の黒) — ゲーム画面がどんな色でも視認できるようにするため。
        Sdl.SDL_SetRenderDrawColor(_renderer, 0, 0, 0, 160);
        var backRect = new Sdl.SDL_FRect { X = boxX, Y = boxY, W = boxW, H = boxH };
        Sdl.SDL_RenderFillRect(_renderer, ref backRect);

        // 前景(黄色。黒背景との組み合わせでコントラストを確保)。
        Sdl.SDL_SetRenderDrawColor(_renderer, 255, 210, 0, 255);

        float symbolX = boxX + pad;
        float symbolY = boxY + pad + (digitH - symbolH) / 2f; // 数字と縦方向中央を揃える
        DrawMultiplySymbol(symbolX, symbolY, symbolW, symbolH);

        float digitX = symbolX + symbolW + gap;
        float digitY = boxY + pad;
        DrawSevenSegmentDigit(digit, digitX, digitY, digitW, digitH, thickness);

        // 描画状態(ブレンドモード)を元に戻す。
        Sdl.SDL_SetRenderDrawBlendMode(_renderer, Sdl.SDL_BLENDMODE_NONE);
    }

    /// <summary>(x, y)を左上とする幅w・高さhの矩形領域に、を拡大して描画する(「×」記号。ドット絵を単純な矩形の集合として描くだけなのでフォントは不要)。</summary>
    private void DrawMultiplySymbol(float x, float y, float w, float h)
    {
        int rows = MultiplySymbolGlyph.GetLength(0);
        int cols = MultiplySymbolGlyph.GetLength(1);
        float cellW = w / cols;
        float cellH = h / rows;

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < cols; col++)
            {
                if (!MultiplySymbolGlyph[row, col]) continue;
                var rect = new Sdl.SDL_FRect
                {
                    X = x + col * cellW,
                    Y = y + row * cellH,
                    W = cellW,
                    H = cellH,
                };
                Sdl.SDL_RenderFillRect(_renderer, ref rect);
            }
        }
    }

    /// <summary>(x, y)を左上とする幅w・高さhの矩形領域に、7セグメント風の数字digit(0〜9)を描く。</summary>
    private void DrawSevenSegmentDigit(int digit, float x, float y, float w, float h, float thickness)
    {
        if (digit < 0 || digit > 9) return;
        bool[] segs = SevenSegmentTable[digit];
        float t = thickness;
        float halfH = (h - t) / 2f;

        void Fill(float rx, float ry, float rw, float rh)
        {
            if (rw <= 0 || rh <= 0) return;
            var r = new Sdl.SDL_FRect { X = x + rx, Y = y + ry, W = rw, H = rh };
            Sdl.SDL_RenderFillRect(_renderer, ref r);
        }

        if (segs[0]) Fill(t, 0, w - 2 * t, t);                             // a: 上
        if (segs[1]) Fill(w - t, t, t, halfH - t * 0.5f);                  // b: 右上
        if (segs[2]) Fill(w - t, h / 2f + t * 0.5f, t, halfH - t * 0.5f);  // c: 右下
        if (segs[3]) Fill(t, h - t, w - 2 * t, t);                         // d: 下
        if (segs[4]) Fill(0, h / 2f + t * 0.5f, t, halfH - t * 0.5f);      // e: 左下
        if (segs[5]) Fill(0, t, t, halfH - t * 0.5f);                     // f: 左上
        if (segs[6]) Fill(t, h / 2f - t / 2f, w - 2 * t, t);               // g: 中央
    }

    /// <summary>選択中の画質設定が何倍角のスケーラーかを返す(「なし」は1=等倍)。</summary>
    private static int GetPictureQualityScaleFactor(PictureQualityMode mode) => mode switch
    {
        PictureQualityMode.HighQuality3x => 3,
        PictureQualityMode.Hq2x => 2,
        _ => 1,
    };

    /// <summary>_hqScaleBufferを現在の入力サイズ/ピクセル幅/倍角に合わせて確保し直す(必要な時だけ)。</summary>
    private void EnsureHqScaleBuffer(int width, int height, int bpp, int scaleFactor)
    {
        if (_hqScaleSrcW == width && _hqScaleSrcH == height && _hqScaleBpp == bpp && _hqScaleFactor == scaleFactor
            && _hqScaleBuffer.Length == width * scaleFactor * height * scaleFactor * bpp)
        {
            return;
        }

        _hqScaleSrcW = width;
        _hqScaleSrcH = height;
        _hqScaleBpp = bpp;
        _hqScaleFactor = scaleFactor;
        _hqScaleBuffer = new byte[width * scaleFactor * height * scaleFactor * bpp];
    }

    /// <summary>独自実装のHigh Quality 3×スケーラー(Scale3x/AdvMAME3x系アルゴリズム)。</summary>
    private static unsafe void ApplyHighQuality3xScale(byte* src, int srcPitch, int width, int height, int bpp, byte[] dst)
    {
        const int scaleFactor = 3;
        int dstWidth = width * scaleFactor;
        int dstPitch = dstWidth * bpp;

        fixed (byte* dstBase = dst)
        {
            for (int y = 0; y < height; y++)
            {
                int yUp = y > 0 ? y - 1 : 0;
                int yDown = y < height - 1 ? y + 1 : height - 1;

                for (int x = 0; x < width; x++)
                {
                    int xLeft = x > 0 ? x - 1 : 0;
                    int xRight = x < width - 1 ? x + 1 : width - 1;

                    uint a = ReadHqPixel(src, srcPitch, bpp, xLeft, yUp);
                    uint b = ReadHqPixel(src, srcPitch, bpp, x, yUp);
                    uint c = ReadHqPixel(src, srcPitch, bpp, xRight, yUp);
                    uint d = ReadHqPixel(src, srcPitch, bpp, xLeft, y);
                    uint e = ReadHqPixel(src, srcPitch, bpp, x, y);
                    uint f = ReadHqPixel(src, srcPitch, bpp, xRight, y);
                    uint g = ReadHqPixel(src, srcPitch, bpp, xLeft, yDown);
                    uint h = ReadHqPixel(src, srcPitch, bpp, x, yDown);
                    uint i = ReadHqPixel(src, srcPitch, bpp, xRight, yDown);

                    uint e0 = (d == b && d != h && b != f) ? d : e;
                    uint e1 = ((d == b && d != h && b != f && e != c) || (b == f && b != d && f != h && e != a)) ? b : e;
                    uint e2 = (b == f && b != d && f != h) ? f : e;
                    uint e3 = ((d == h && d != b && h != f && e != a) || (d == b && d != h && b != f && e != g)) ? d : e;
                    uint e5 = ((b == f && b != d && f != h && e != i) || (h == f && h != d && f != b && e != c)) ? f : e;
                    uint e6 = (d == h && d != b && h != f) ? d : e;
                    uint e7 = ((h == f && h != d && f != b && e != g) || (d == h && d != b && h != f && e != i)) ? h : e;
                    uint e8 = (h == f && h != d && f != b) ? f : e;

                    int ox = x * scaleFactor;
                    int oy = y * scaleFactor;
                    WriteHqPixel(dstBase, dstPitch, bpp, ox, oy, e0);
                    WriteHqPixel(dstBase, dstPitch, bpp, ox + 1, oy, e1);
                    WriteHqPixel(dstBase, dstPitch, bpp, ox + 2, oy, e2);
                    WriteHqPixel(dstBase, dstPitch, bpp, ox, oy + 1, e3);
                    WriteHqPixel(dstBase, dstPitch, bpp, ox + 1, oy + 1, e);
                    WriteHqPixel(dstBase, dstPitch, bpp, ox + 2, oy + 1, e5);
                    WriteHqPixel(dstBase, dstPitch, bpp, ox, oy + 2, e6);
                    WriteHqPixel(dstBase, dstPitch, bpp, ox + 1, oy + 2, e7);
                    WriteHqPixel(dstBase, dstPitch, bpp, ox + 2, oy + 2, e8);
                }
            }
        }
    }

    /// <summary>(x,y)のピクセルを生のビット列のまま(2バイトなら0〜65535, 4バイトなら0〜4294967295へゼロ拡張して)読む。</summary>
    private static unsafe uint ReadHqPixel(byte* src, int pitch, int bpp, int x, int y)
    {
        byte* p = src + (long)y * pitch + (long)x * bpp;
        return bpp == 2 ? *(ushort*)p : *(uint*)p;
    }

    /// <summary>ReadHqPixelでゼロ拡張した値を、元のバイト幅へ切り詰めて書き戻す(情報の欠落は無い)。</summary>
    private static unsafe void WriteHqPixel(byte* dst, int pitch, int bpp, int x, int y, uint value)
    {
        byte* p = dst + (long)y * pitch + (long)x * bpp;
        if (bpp == 2) *(ushort*)p = (ushort)value;
        else *(uint*)p = value;
    }

    // 画質: 独自実装のHQ2x風スケーラー(Hq2x)本家hq2x(Maxim Stepin氏による、256パターンの参照テーブルとYUV閾値判定を使う実装)そのものを移植したものではなく、「輪郭の近傍ピクセルをビット完全一致ではなくRGB空間での知覚的な近さで判定し、コピーではなくブレンドで滑らかにする」という考え方だけを踏まえて独自に設計した簡易版。

    /// <summary>2色の近さをRGBの重み付き二乗距離で判定するための閾値。</summary>
    private const int Hq2xCloseThresholdSq = 2200;

    private unsafe void ApplyHq2xScale(byte* src, int srcPitch, int width, int height, int bpp, byte[] dst)
    {
        const int scaleFactor = 2;
        int dstWidth = width * scaleFactor;
        int dstPitch = dstWidth * bpp;

        fixed (byte* dstBase = dst)
        {
            for (int y = 0; y < height; y++)
            {
                int yUp = y > 0 ? y - 1 : 0;
                int yDown = y < height - 1 ? y + 1 : height - 1;
                byte* dstRow0 = dstBase + (long)(y * scaleFactor) * dstPitch;
                byte* dstRow1 = dstBase + (long)(y * scaleFactor + 1) * dstPitch;

                for (int x = 0; x < width; x++)
                {
                    int xLeft = x > 0 ? x - 1 : 0;
                    int xRight = x < width - 1 ? x + 1 : width - 1;

                    // 3x3近傍(中心E、四隅は今回のルールでは未使用のため取得しない)。
                    var b = DecodePixelAt(src, srcPitch, bpp, x, yUp);
                    var d = DecodePixelAt(src, srcPitch, bpp, xLeft, y);
                    var e = DecodePixelAt(src, srcPitch, bpp, x, y);
                    var f = DecodePixelAt(src, srcPitch, bpp, xRight, y);
                    var h = DecodePixelAt(src, srcPitch, bpp, x, yDown);

                    bool db = IsPerceptuallyClose(d, b);
                    bool bf = IsPerceptuallyClose(b, f);
                    bool dh = IsPerceptuallyClose(d, h);
                    bool fh = IsPerceptuallyClose(f, h);

                    // 各出力コーナーについて、その角に接する2辺(例: 左上ならD,B)が互いに近い色 かつ 反対側の辺の組(H,Fなど)とは近くない = そこに斜めの輪郭が通っていると判断できる時だけ、辺2色+中心色をブレンドして滑らかにする。
                    var e0 = (db && !dh && !bf) ? Blend3(d, b, e, 3, 3, 2) : e; // 左上
                    var e1 = (bf && !db && !fh) ? Blend3(b, f, e, 3, 3, 2) : e; // 右上
                    var e2 = (dh && !db && !fh) ? Blend3(d, h, e, 3, 3, 2) : e; // 左下
                    var e3 = (fh && !bf && !dh) ? Blend3(f, h, e, 3, 3, 2) : e; // 右下

                    int ox = x * scaleFactor;
                    EncodePixel(dstRow0, ox, bpp, e0.r, e0.g, e0.b);
                    EncodePixel(dstRow0, ox + 1, bpp, e1.r, e1.g, e1.b);
                    EncodePixel(dstRow1, ox, bpp, e2.r, e2.g, e2.b);
                    EncodePixel(dstRow1, ox + 1, bpp, e3.r, e3.g, e3.b);
                }
            }
        }
    }

    /// <summary>srcBase(1行目先頭)からsrcPitch/bppを使って(x,y)のピクセルをRGB8へデコードする。</summary>
    private unsafe (byte r, byte g, byte b) DecodePixelAt(byte* srcBase, int srcPitch, int bpp, int x, int y)
    {
        byte* row = srcBase + (long)y * srcPitch;
        DecodePixel(row, x, bpp, out byte r, out byte g, out byte b);
        return (r, g, b);
    }

    /// <summary>2色が「知覚的に近い」かどうかを判定する。</summary>
    private static bool IsPerceptuallyClose((byte r, byte g, byte b) p1, (byte r, byte g, byte b) p2)
    {
        int dr = p1.r - p2.r;
        int dg = p1.g - p2.g;
        int db = p1.b - p2.b;
        int distSq = (dr * dr * 2) + (dg * dg * 4) + (db * db * 3);
        return distSq <= Hq2xCloseThresholdSq;
    }

    /// <summary>3色を指定の重みで加重平均する(端数は四捨五入)。</summary>
    private static (byte r, byte g, byte b) Blend3(
        (byte r, byte g, byte b) c1, (byte r, byte g, byte b) c2, (byte r, byte g, byte b) c3,
        int w1, int w2, int w3)
    {
        int sum = w1 + w2 + w3;
        int half = sum / 2;
        byte r = (byte)Math.Clamp((c1.r * w1 + c2.r * w2 + c3.r * w3 + half) / sum, 0, 255);
        byte g = (byte)Math.Clamp((c1.g * w1 + c2.g * w2 + c3.g * w3 + half) / sum, 0, 255);
        byte b = (byte)Math.Clamp((c1.b * w1 + c2.b * w2 + c3.b * w3 + half) / sum, 0, 255);
        return (r, g, b);
    }

    /// <summary>DecodePixelの逆変換。</summary>
    private unsafe void EncodePixel(byte* row, int x, int bytesPerPixel, byte r, byte g, byte b)
    {
        if (bytesPerPixel == 4)
        {
            // XRGB8888 (SDL_PIXELFORMAT_ARGB8888, リトルエンディアン): メモリ順は B,G,R,X
            byte* p = row + (long)x * 4;
            p[0] = b;
            p[1] = g;
            p[2] = r;
            p[3] = 0;
            return;
        }

        byte* p16 = row + (long)x * 2;
        int v;
        if (_pixelFormat == RetroPixelFormat.RGB565)
        {
            int r5 = r * 31 / 255;
            int g6 = g * 63 / 255;
            int b5 = b * 31 / 255;
            v = (r5 << 11) | (g6 << 5) | b5;
        }
        else
        {
            // 0RGB1555
            int r5 = r * 31 / 255;
            int g5 = g * 31 / 255;
            int b5 = b * 31 / 255;
            v = (r5 << 10) | (g5 << 5) | b5;
        }

        p16[0] = (byte)(v & 0xFF);
        p16[1] = (byte)((v >> 8) & 0xFF);
    }

    private bool RecreateGlContext(RetroHwContextType contextType, uint versionMajor, uint versionMinor)
    {
        if (_glContext != nint.Zero)
        {
            Sdl.SDL_GL_MakeCurrent(_sdlWindow, nint.Zero);
            Sdl.SDL_GL_DestroyContext(_glContext);
            _glContext = nint.Zero;
        }

        // プロファイル/バージョンを明示してから作り直す
        int profileMask = contextType switch
        {
            RetroHwContextType.OPENGL_CORE => Sdl.SDL_GL_CONTEXT_PROFILE_CORE,
            RetroHwContextType.OPENGLES2
                or RetroHwContextType.OPENGLES3 => Sdl.SDL_GL_CONTEXT_PROFILE_ES,
            _ => Sdl.SDL_GL_CONTEXT_PROFILE_COMPATIBILITY,
        };

        // libretroの version_major/minor は「最低限必要なバージョン」であり、それ以上を返してよい(RetroArchのWGL/EGL実装も同様)。
        var candidates = new List<(int Major, int Minor)>();
        if (contextType == RetroHwContextType.OPENGL_CORE && versionMajor > 0)
        {
            foreach (var v in new[] { (4, 6), (4, 5), (4, 3) })
            {
                if (v.Item1 > versionMajor || (v.Item1 == versionMajor && v.Item2 > versionMinor))
                    candidates.Add(v);
            }
        }
        candidates.Add(((int)versionMajor, (int)versionMinor));

        _glContext = nint.Zero;
        foreach (var (major, minor) in candidates)
        {
            Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_CONTEXT_PROFILE_MASK, profileMask);

            if (major > 0)
            {
                Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_CONTEXT_MAJOR_VERSION, major);
                Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_CONTEXT_MINOR_VERSION, minor);
            }

            Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_DOUBLEBUFFER, 1);
            Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_DEPTH_SIZE, 24);
            Sdl.SDL_GL_SetAttribute(Sdl.SDL_GL_STENCIL_SIZE, 8);

            _glContext = Sdl.SDL_GL_CreateContext(_sdlWindow);
            if (_glContext != nint.Zero)
            {
                if (Sdl.SDL_GL_MakeCurrent(_sdlWindow, _glContext))
                {
                    System.Diagnostics.Debug.WriteLine($"[EmulationHost] GLコンテキスト作成成功: {contextType} {major}.{minor} (コア要求: {versionMajor}.{versionMinor})");
                    break;
                }

                ReportStatus("SDL_GL_MakeCurrent(再作成後)に失敗: " + Sdl.SDL_GetErrorString());
                Sdl.SDL_GL_DestroyContext(_glContext);
                _glContext = nint.Zero;
            }
            else
            {
                System.Diagnostics.Debug.WriteLine($"[EmulationHost] GLコンテキスト作成失敗: {contextType} {major}.{minor}: {Sdl.SDL_GetErrorString()}");
            }
        }

        if (_glContext == nint.Zero)
        {
            ReportStatus("SDL_GL_CreateContext(再作成)に失敗: " + Sdl.SDL_GetErrorString());
            return false;
        }

        Sdl.SDL_GL_SetSwapInterval(0);
        return true;
    }

}
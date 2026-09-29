// CPU reference compositor. Preview and export share Compose; GPU acceleration remains a release gate.
// Included inside the capture translation unit's private namespace after VideoWriter.
thread_local uint32_t render_stage = 0;
struct DecodedImage { int64_t pts = -1; std::vector<uint8_t> nv12; };
class RenderReader {
    com_ptr<IMFSourceReader> reader;
    uint32_t width, height, coded_width = 0, coded_height = 0, offset_x = 0, offset_y = 0;
    DecodedImage current, next;
    bool ended = false;
    int64_t last_read = -1, last_request = -1;
    bool Read(DecodedImage& image) {
        while (!ended) {
            render_stage = 6;
            DWORD flags = 0; LONGLONG pts = 0; com_ptr<IMFSample> sample;
            check_hresult(reader->ReadSample(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), 0, nullptr, &flags, &pts, sample.put()));
            if (flags & MF_SOURCE_READERF_ERROR) throw hresult_error(E_FAIL);
            if (flags & MF_SOURCE_READERF_CURRENTMEDIATYPECHANGED) CheckType();
            ended = (flags & MF_SOURCE_READERF_ENDOFSTREAM) != 0;
            if (!sample) continue;
            if (pts < 0 || pts <= last_read) throw hresult_error(MF_E_INVALID_TIMESTAMP);
            last_read = pts; image.pts = pts;
            image.nv12.resize(static_cast<size_t>(width) * height * 3 / 2);
            render_stage = 7;
            com_ptr<IMFMediaBuffer> buffer; check_hresult(sample->ConvertToContiguousBuffer(buffer.put()));
            if (auto two = buffer.try_as<IMF2DBuffer>()) {
                BYTE* data = nullptr; LONG pitch = 0;
                check_hresult(two->Lock2D(&data, &pitch));
                render_stage = 8;
                if (pitch < static_cast<LONG>(coded_width)) { two->Unlock2D(); throw hresult_error(MF_E_INVALIDMEDIATYPE); }
                for (uint32_t y = 0; y < height; ++y)
                    memcpy(image.nv12.data() + static_cast<size_t>(y) * width, data + static_cast<size_t>(y + offset_y) * pitch + offset_x, width);
                for (uint32_t y = 0; y < height / 2; ++y)
                    memcpy(image.nv12.data() + static_cast<size_t>(height + y) * width, data + static_cast<size_t>(coded_height + y + offset_y / 2) * pitch + offset_x, width);
                check_hresult(two->Unlock2D());
            } else {
                BYTE* data = nullptr; DWORD bytes = 0;
                check_hresult(buffer->Lock(&data, nullptr, &bytes));
                render_stage = 9;
                if (bytes != static_cast<size_t>(coded_width) * coded_height * 3 / 2) { buffer->Unlock(); throw hresult_error(MF_E_INVALIDMEDIATYPE); }
                for (uint32_t y = 0; y < height; ++y)
                    memcpy(image.nv12.data() + static_cast<size_t>(y) * width, data + static_cast<size_t>(y + offset_y) * coded_width + offset_x, width);
                for (uint32_t y = 0; y < height / 2; ++y)
                    memcpy(image.nv12.data() + static_cast<size_t>(height + y) * width, data + static_cast<size_t>(coded_height + y + offset_y / 2) * coded_width + offset_x, width);
                check_hresult(buffer->Unlock());
            }
            return true;
        }
        image = {}; return false;
    }
    void CheckType() {
        render_stage = 3;
        com_ptr<IMFMediaType> actual;
        check_hresult(reader->GetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), actual.put()));
        UINT32 aw = 0, ah = 0; GUID subtype{};
        check_hresult(MFGetAttributeSize(actual.get(), MF_MT_FRAME_SIZE, &aw, &ah));
        check_hresult(actual->GetGUID(MF_MT_SUBTYPE, &subtype));
        if (aw < width || ah < height || aw > 7680 || ah > 4352 || ((aw | ah) & 1) || subtype != MFVideoFormat_NV12) throw hresult_error(MF_E_INVALIDMEDIATYPE);
        coded_width = aw; coded_height = ah; offset_x = offset_y = 0;
        if (aw != width || ah != height) {
            render_stage = 10;
            MFVideoArea area{}; UINT32 bytes = 0;
            HRESULT aperture = actual->GetBlob(MF_MT_MINIMUM_DISPLAY_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), &bytes);
            if (FAILED(aperture)) aperture = actual->GetBlob(MF_MT_GEOMETRIC_APERTURE, reinterpret_cast<UINT8*>(&area), sizeof(area), &bytes);
            check_hresult(aperture);
            if (bytes != sizeof(area) || area.Area.cx != static_cast<LONG>(width) || area.Area.cy != static_cast<LONG>(height) ||
                area.OffsetX.value < 0 || area.OffsetY.value < 0 || area.OffsetX.fract || area.OffsetY.fract || ((area.OffsetX.value | area.OffsetY.value) & 1))
                throw hresult_error(MF_E_INVALIDMEDIATYPE);
            offset_x = static_cast<uint32_t>(area.OffsetX.value); offset_y = static_cast<uint32_t>(area.OffsetY.value);
            if (offset_x + width > aw || offset_y + height > ah) throw hresult_error(MF_E_INVALIDMEDIATYPE);
        }
        render_stage = 4;
        UINT32 matrix = 0, range = 0, primaries = 0, transfer = 0;
        check_hresult(actual->GetUINT32(MF_MT_YUV_MATRIX, &matrix));
        check_hresult(actual->GetUINT32(MF_MT_VIDEO_NOMINAL_RANGE, &range));
        check_hresult(actual->GetUINT32(MF_MT_VIDEO_PRIMARIES, &primaries));
        check_hresult(actual->GetUINT32(MF_MT_TRANSFER_FUNCTION, &transfer));
        if (matrix != MFVideoTransferMatrix_BT709 || range != MFNominalRange_16_235 || primaries != MFVideoPrimaries_BT709 || transfer != MFVideoTransFunc_709) throw hresult_error(MF_E_INVALIDMEDIATYPE);
    }
public:
    RenderReader(const wchar_t* path, uint32_t w, uint32_t h) : width(w), height(h) {
        render_stage = 1;
        check_hresult(MFCreateSourceReaderFromURL(path, nullptr, reader.put()));
        check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_ALL_STREAMS), FALSE));
        check_hresult(reader->SetStreamSelection(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), TRUE));
        com_ptr<IMFMediaType> type;
        render_stage = 2;
        check_hresult(MFCreateMediaType(type.put()));
        check_hresult(type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video));
        check_hresult(type->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12));
        check_hresult(reader->SetCurrentMediaType(static_cast<DWORD>(MF_SOURCE_READER_FIRST_VIDEO_STREAM), nullptr, type.get()));
        CheckType();
        if (!Read(current) || current.pts > 333'340) throw hresult_error(MF_E_INVALID_TIMESTAMP);
        Read(next);
    }
    const DecodedImage& At(int64_t us) {
        if (us < last_request) throw hresult_error(E_INVALIDARG);
        last_request = us;
        while (next.pts >= 0 && next.pts <= us * 10) { current = std::move(next); Read(next); }
        return current;
    }
};

void ValidateGeometry(uint32_t sw, uint32_t sh, uint32_t w, uint32_t h) {
    if (sw < 2 || sh < 2 || sw > 7680 || sh > 4320 || (sw | sh) % 2 ||
        w < 2 || h < 2 || w > 3840 || h > 3840 || (w | h) % 2 || static_cast<uint64_t>(w) * h > 8'294'400)
        throw hresult_error(E_INVALIDARG);
}
void ValidateFrame(const GlideRenderFrame& f, uint32_t w, uint32_t h) {
    const double values[]{f.crop_x, f.crop_y, f.crop_w, f.crop_h, f.dest_x, f.dest_y, f.dest_w, f.dest_h, f.cursor_x, f.cursor_y, f.cursor_scale, f.corner_radius, f.click_amount, f.click_x, f.click_y};
    for (double value : values) if (!std::isfinite(value)) throw hresult_error(E_INVALIDARG);
    constexpr double epsilon = 1e-7;
    if (f.source_us < 0 || f.source_us > 1'800'100'000 || f.output_us < 0 || f.crop_x < -epsilon || f.crop_y < -epsilon ||
        f.crop_w < .25 || f.crop_h < .25 || f.crop_x + f.crop_w > 1 + epsilon || f.crop_y + f.crop_h > 1 + epsilon ||
        f.dest_x < 0 || f.dest_y < 0 || f.dest_w < 1 || f.dest_h < 1 || f.dest_x + f.dest_w > w + epsilon || f.dest_y + f.dest_h > h + epsilon ||
        f.cursor_scale < .5 || f.cursor_scale > 4 || f.cursor_visible > 1 || f.background_argb >> 24 != 255 || f.corner_radius < 0 || f.corner_radius > 320 || f.click_amount < 0 || f.click_amount > 1 ||
        (f.cursor_visible && (f.cursor_x < -epsilon || f.cursor_y < -epsilon || f.cursor_x > w + epsilon || f.cursor_y > h + epsilon)))
        throw hresult_error(E_INVALIDARG);
}
std::vector<uint8_t> ToBgra(const DecodedImage& image, uint32_t w, uint32_t h) {
    std::vector<uint8_t> pixels(static_cast<size_t>(w) * h * 4);
    const auto* uv = image.nv12.data() + static_cast<size_t>(w) * h;
    auto byte = [](int n) { return static_cast<uint8_t>(std::clamp((n + 32768) >> 16, 0, 255)); };
    for (uint32_t y = 0; y < h; ++y) for (uint32_t x = 0; x < w; ++x) {
        int l = 76309 * (image.nv12[static_cast<size_t>(y) * w + x] - 16);
        size_t c = static_cast<size_t>(y / 2) * w + (x & ~1u);
        int u = uv[c] - 128, v = uv[c + 1] - 128;
        auto p = pixels.data() + (static_cast<size_t>(y) * w + x) * 4;
        p[0] = byte(l + 138438 * u); p[1] = byte(l - 13975 * u - 34925 * v); p[2] = byte(l + 117489 * v); p[3] = 255;
    }
    return pixels;
}
// Pixel-center bilinear sampling, with the cursor hotspot at the exact RenderPlan coordinate.
void Compose(const std::vector<uint8_t>& source, uint32_t sw, uint32_t sh, const GlideRenderFrame& f, uint32_t w, uint32_t h, uint8_t* out) {
    for (size_t i = 0; i < static_cast<size_t>(w) * h; ++i) {
        out[i * 4] = static_cast<uint8_t>(f.background_argb); out[i * 4 + 1] = static_cast<uint8_t>(f.background_argb >> 8);
        out[i * 4 + 2] = static_cast<uint8_t>(f.background_argb >> 16); out[i * 4 + 3] = 255;
    }
    for (uint32_t y = 0; y < h; ++y) {
        double dy = (y + .5 - f.dest_y) / f.dest_h;
        if (dy < 0 || dy >= 1) continue;
        double sy = std::clamp((f.crop_y + dy * f.crop_h) * sh - .5, 0.0, sh - 1.0);
        uint32_t y0 = static_cast<uint32_t>(sy), y1 = std::min(y0 + 1, sh - 1); double fy = sy - y0;
        for (uint32_t x = 0; x < w; ++x) {
            double dx = (x + .5 - f.dest_x) / f.dest_w;
            if (dx < 0 || dx >= 1) continue;
            double radius = std::min(f.corner_radius, std::min(f.dest_w, f.dest_h) / 2);
            double edgeX = std::max(radius - std::min(x + .5 - f.dest_x, f.dest_x + f.dest_w - x - .5), 0.0);
            double edgeY = std::max(radius - std::min(y + .5 - f.dest_y, f.dest_y + f.dest_h - y - .5), 0.0);
            if (edgeX * edgeX + edgeY * edgeY > radius * radius) continue;
            double sx = std::clamp((f.crop_x + dx * f.crop_w) * sw - .5, 0.0, sw - 1.0);
            uint32_t x0 = static_cast<uint32_t>(sx), x1 = std::min(x0 + 1, sw - 1); double fx = sx - x0;
            for (uint32_t c = 0; c < 3; ++c) {
                auto pixel = [&](uint32_t px, uint32_t py) { return source[(static_cast<size_t>(py) * sw + px) * 4 + c]; };
                double top = pixel(x0, y0) * (1 - fx) + pixel(x1, y0) * fx;
                double bottom = pixel(x0, y1) * (1 - fx) + pixel(x1, y1) * fx;
                out[(static_cast<size_t>(y) * w + x) * 4 + c] = static_cast<uint8_t>(std::lround(top * (1 - fy) + bottom * fy));
            }
        }
    }
    if (f.click_amount > 0) {
        double radius = (12 + 12 * (1 - f.click_amount)) * h / 1080.0;
        for (int y = std::max(0, static_cast<int>(f.click_y - radius - 2)); y < std::min(static_cast<int>(h), static_cast<int>(f.click_y + radius + 2)); ++y)
        for (int x = std::max(0, static_cast<int>(f.click_x - radius - 2)); x < std::min(static_cast<int>(w), static_cast<int>(f.click_x + radius + 2)); ++x) {
            if (x < f.dest_x || x >= f.dest_x + f.dest_w || y < f.dest_y || y >= f.dest_y + f.dest_h) continue;
            double alpha = std::clamp(2 - std::abs(std::hypot(x + .5 - f.click_x, y + .5 - f.click_y) - radius), 0.0, 1.0) * f.click_amount;
            auto pixel = out + (static_cast<size_t>(y) * w + x) * 4;
            const double color[]{180, 216, 73};
            for (int c = 0; c < 3; ++c) pixel[c] = static_cast<uint8_t>(std::lround(pixel[c] * (1 - alpha) + color[c] * alpha));
        }
    }
    if (!f.cursor_visible) return;
    // Four-sample coverage for an outlined arrow; custom captured shapes remain a future capability.
    static constexpr double ax[]{0, 0, 5, 9, 12, 8, 15};
    static constexpr double ay[]{0, 21, 16, 25, 24, 15, 15};
    double scale = f.cursor_scale * h / 1080.0;
    int left = std::max(0, static_cast<int>(std::floor(f.cursor_x - scale)));
    int top = std::max(0, static_cast<int>(std::floor(f.cursor_y - scale)));
    int right = std::min(static_cast<int>(w), static_cast<int>(std::ceil(f.cursor_x + 17 * scale)));
    int bottom = std::min(static_cast<int>(h), static_cast<int>(std::ceil(f.cursor_y + 27 * scale)));
    for (int y = top; y < bottom; ++y) for (int x = left; x < right; ++x) {
        double cover = 0, light = 0;
        for (double oy : {.25, .75}) for (double ox : {.25, .75}) {
            if (x + ox < f.dest_x || x + ox >= f.dest_x + f.dest_w || y + oy < f.dest_y || y + oy >= f.dest_y + f.dest_h) continue;
            double px = (x + ox - f.cursor_x) / scale, py = (y + oy - f.cursor_y) / scale;
            bool inside = false; double distance = 1e9;
            for (int i = 0, j = 6; i < 7; j = i++) {
                if ((ay[i] > py) != (ay[j] > py) && px < (ax[j] - ax[i]) * (py - ay[i]) / (ay[j] - ay[i]) + ax[i]) inside = !inside;
                double vx = ax[j] - ax[i], vy = ay[j] - ay[i];
                double t = std::clamp(((px - ax[i]) * vx + (py - ay[i]) * vy) / (vx * vx + vy * vy), 0.0, 1.0);
                distance = std::min(distance, std::hypot(px - ax[i] - t * vx, py - ay[i] - t * vy));
            }
            if (inside || distance <= .8) { cover += .25; light += (inside && distance > .8 ? 250 : 15) * .25; }
        }
        auto pixel = out + (static_cast<size_t>(y) * w + x) * 4;
        for (int c = 0; c < 3; ++c) pixel[c] = static_cast<uint8_t>(std::lround(pixel[c] * (1 - cover) + light));
    }
}

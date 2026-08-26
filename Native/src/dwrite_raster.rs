use std::mem::ManuallyDrop;

use windows::Win32::Foundation::{BOOL, RECT};
use windows::Win32::Graphics::DirectWrite::{
    DWRITE_FACTORY_TYPE_SHARED, DWRITE_FONT_METRICS, DWRITE_FONT_STRETCH_NORMAL,
    DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_WEIGHT_BOLD, DWRITE_FONT_WEIGHT_NORMAL,
    DWRITE_GLYPH_METRICS, DWRITE_GLYPH_OFFSET, DWRITE_GLYPH_RUN, DWRITE_GRID_FIT_MODE_ENABLED,
    DWRITE_MATRIX, DWRITE_MEASURING_MODE, DWRITE_MEASURING_MODE_GDI_CLASSIC,
    DWRITE_MEASURING_MODE_NATURAL, DWRITE_RENDERING_MODE, DWRITE_RENDERING_MODE_GDI_CLASSIC,
    DWRITE_RENDERING_MODE_NATURAL, DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC,
    DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE, DWRITE_TEXTURE_ALIASED_1x1, DWriteCreateFactory,
    IDWriteFactory, IDWriteFactory2, IDWriteFontCollection, IDWriteFontFace,
    IDWriteLocalizedStrings,
};
use windows::core::{HSTRING, Interface, PCWSTR};

use crate::raster::{RasterImage, TextRasterRequest};

struct GlyphMask {
    bounds: RECT,
    alpha: Vec<u8>,
}

pub fn font_families() -> Result<Vec<String>, String> {
    let factory: IDWriteFactory = unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED) }
        .map_err(|error| format!("cannot create DirectWrite factory: {error}"))?;
    let collection = system_font_collection(&factory)?;
    let mut families = Vec::new();
    for family_index in 0..unsafe { collection.GetFontFamilyCount() } {
        let Ok(family) = (unsafe { collection.GetFontFamily(family_index) }) else {
            continue;
        };
        let Ok(names) = (unsafe { family.GetFamilyNames() }) else {
            continue;
        };
        let Some(name) = localized_string(&names, 0) else {
            continue;
        };
        if name.trim().is_empty()
            || font_face(&collection, &name, false).is_err()
            || font_face(&collection, &name, true).is_err()
        {
            continue;
        }
        families.push(name);
    }
    families.sort_by_key(|name| name.to_lowercase());
    families.dedup_by(|left, right| left.eq_ignore_ascii_case(right));
    Ok(families)
}

fn localized_string(strings: &IDWriteLocalizedStrings, index: u32) -> Option<String> {
    if index >= unsafe { strings.GetCount() } {
        return None;
    }
    let length = unsafe { strings.GetStringLength(index) }.ok()? as usize;
    let mut buffer = vec![0u16; length + 1];
    unsafe { strings.GetString(index, &mut buffer) }.ok()?;
    String::from_utf16(&buffer[..length]).ok()
}

pub fn rasterize_text(request: &TextRasterRequest) -> Result<RasterImage, String> {
    if !request.font_file.trim().is_empty() {
        return Err("DirectWrite custom font files are not implemented".to_owned());
    }

    let factory: IDWriteFactory = unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED) }
        .map_err(|error| format!("cannot create DirectWrite factory: {error}"))?;
    let factory2: IDWriteFactory2 = factory
        .cast()
        .map_err(|error| format!("DirectWrite 2 is unavailable: {error}"))?;
    let face = system_font_face(&factory, request)?;
    let small_regular = !request.bold && request.pixel_size <= 20;
    let gdi_classic = request.bold && request.pixel_size <= 20;
    let rendering_mode = if gdi_classic {
        DWRITE_RENDERING_MODE_GDI_CLASSIC
    } else if small_regular {
        DWRITE_RENDERING_MODE_NATURAL
    } else {
        DWRITE_RENDERING_MODE_NATURAL_SYMMETRIC
    };
    let measuring_mode = if gdi_classic {
        DWRITE_MEASURING_MODE_GDI_CLASSIC
    } else {
        DWRITE_MEASURING_MODE_NATURAL
    };
    let identity = identity_matrix();
    let mut font_metrics = DWRITE_FONT_METRICS::default();
    if gdi_classic {
        unsafe {
            face.GetGdiCompatibleMetrics(
                request.pixel_size as f32,
                1.0,
                Some(&identity),
                &mut font_metrics,
            )
        }
        .map_err(|error| format!("cannot get DirectWrite GDI font metrics: {error}"))?;
    } else {
        unsafe { face.GetMetrics(&mut font_metrics) };
    }
    let units_per_em = font_metrics.designUnitsPerEm as f32;
    if units_per_em <= 0.0 {
        return Err("DirectWrite font reports zero design units per em".to_owned());
    }

    let pixel_size = request.pixel_size as f32;
    let ascent = pixel_size * font_metrics.ascent as f32 / units_per_em;
    let descent = pixel_size * font_metrics.descent as f32 / units_per_em;
    let font_height = ascent + descent;
    let normalized = request.text.replace('\r', "");
    let lines: Vec<&str> = normalized.split('\n').collect();
    let mut masks = Vec::new();
    let mut layout_width = 0.0f32;

    for (line_index, line) in lines.iter().enumerate() {
        let codepoints: Vec<u32> = line.chars().map(u32::from).collect();
        if codepoints.is_empty() {
            continue;
        }
        let mut glyphs = vec![0u16; codepoints.len()];
        unsafe {
            face.GetGlyphIndices(
                codepoints.as_ptr(),
                codepoints.len() as u32,
                glyphs.as_mut_ptr(),
            )
        }
        .map_err(|error| format!("cannot map DirectWrite glyphs: {error}"))?;
        let mut metrics = vec![DWRITE_GLYPH_METRICS::default(); glyphs.len()];
        unsafe {
            face.GetDesignGlyphMetrics(
                glyphs.as_ptr(),
                glyphs.len() as u32,
                metrics.as_mut_ptr(),
                false,
            )
        }
        .map_err(|error| format!("cannot measure DirectWrite glyphs: {error}"))?;

        let baseline = line_index as f32 * request.line_height as f32
            + (request.line_height as f32 - font_height) * 0.5
            + ascent;
        let mut cursor_x = 0.0f32;
        for (glyph_index, (glyph, metric)) in glyphs.iter().zip(metrics.iter()).enumerate() {
            let origin_x = cursor_x.round();
            let origin_y = baseline.round()
                - if gdi_classic && request.pixel_size == 13 {
                    1.0
                } else {
                    0.0
                };
            let ascii_natural = gdi_classic && codepoints[glyph_index] < 0x80;
            if let Some(mask) = render_glyph(
                &factory2,
                &face,
                *glyph,
                pixel_size,
                origin_x,
                origin_y,
                if ascii_natural {
                    DWRITE_RENDERING_MODE_NATURAL
                } else {
                    rendering_mode
                },
                if ascii_natural {
                    DWRITE_MEASURING_MODE_NATURAL
                } else {
                    measuring_mode
                },
            )? {
                masks.push(mask);
            }
            let advance = pixel_size * metric.advanceWidth as f32 / units_per_em;
            cursor_x += advance;
        }
        layout_width = layout_width.max(cursor_x);
    }

    let layout_height = lines.len() as f32 * request.line_height as f32;
    if masks.is_empty() {
        return Ok(RasterImage {
            pixels: Vec::new(),
            width: 0,
            height: 0,
            texture_offset_x: 0.0,
            texture_offset_y: 0.0,
            layout_width,
            layout_height,
            visual_x: 0.0,
            visual_y: 0.0,
            visual_width: 0.0,
            visual_height: 0.0,
        });
    }

    let left = masks.iter().map(|mask| mask.bounds.left).min().unwrap_or(0);
    let top = masks.iter().map(|mask| mask.bounds.top).min().unwrap_or(0);
    let right = masks
        .iter()
        .map(|mask| mask.bounds.right)
        .max()
        .unwrap_or(left);
    let bottom = masks
        .iter()
        .map(|mask| mask.bounds.bottom)
        .max()
        .unwrap_or(top);
    let width = (right - left).max(0) as u32;
    let height = (bottom - top).max(0) as u32;
    let mut pixels = vec![0u8; width as usize * height as usize * 4];
    let color_lut =
        small_regular.then(|| skia_srgb_coverage_lut(request.red, request.green, request.blue));
    for mask in masks {
        composite_mask(
            &mut pixels,
            width,
            left,
            top,
            &mask,
            request.red,
            request.green,
            request.blue,
            color_lut.as_ref(),
        );
    }
    Ok(RasterImage {
        pixels,
        width,
        height,
        texture_offset_x: left as f32,
        texture_offset_y: top as f32,
        layout_width,
        layout_height,
        visual_x: left as f32,
        visual_y: top as f32,
        visual_width: width as f32,
        visual_height: height as f32,
    })
}

fn system_font_face(
    factory: &IDWriteFactory,
    request: &TextRasterRequest,
) -> Result<IDWriteFontFace, String> {
    let collection = system_font_collection(factory)?;
    let requested_name = if request.font_family.trim().is_empty() {
        "Microsoft YaHei UI"
    } else {
        request.font_family.trim()
    };
    if let Ok(face) = font_face(&collection, requested_name, request.bold) {
        return Ok(face);
    }

    for fallback in ["Microsoft YaHei UI", "Segoe UI"] {
        if fallback.eq_ignore_ascii_case(requested_name) {
            continue;
        }
        if let Ok(face) = font_face(&collection, fallback, request.bold) {
            return Ok(face);
        }
    }

    Err(format!(
        "DirectWrite font family '{requested_name}' was not found and no fallback font is available"
    ))
}

fn system_font_collection(factory: &IDWriteFactory) -> Result<IDWriteFontCollection, String> {
    let mut collection = None;
    unsafe { factory.GetSystemFontCollection(&mut collection, false) }
        .map_err(|error| format!("cannot enumerate DirectWrite system fonts: {error}"))?;
    collection.ok_or_else(|| "DirectWrite returned no font collection".to_owned())
}

fn font_face(
    collection: &IDWriteFontCollection,
    family_name: &str,
    bold: bool,
) -> Result<IDWriteFontFace, String> {
    let wide = HSTRING::from(family_name);
    let mut family_index = 0u32;
    let mut exists = BOOL::default();
    unsafe { collection.FindFamilyName(PCWSTR(wide.as_ptr()), &mut family_index, &mut exists) }
        .map_err(|error| format!("cannot find DirectWrite font family '{family_name}': {error}"))?;
    if !exists.as_bool() {
        return Err(format!(
            "DirectWrite font family '{family_name}' was not found"
        ));
    }
    let family = unsafe { collection.GetFontFamily(family_index) }
        .map_err(|error| format!("cannot open DirectWrite font family '{family_name}': {error}"))?;
    let font = unsafe {
        family.GetFirstMatchingFont(
            if bold {
                DWRITE_FONT_WEIGHT_BOLD
            } else {
                DWRITE_FONT_WEIGHT_NORMAL
            },
            DWRITE_FONT_STRETCH_NORMAL,
            DWRITE_FONT_STYLE_NORMAL,
        )
    }
    .map_err(|error| format!("cannot select DirectWrite font '{family_name}': {error}"))?;
    unsafe { font.CreateFontFace() }
        .map_err(|error| format!("cannot create DirectWrite font face '{family_name}': {error}"))
}

fn render_glyph(
    factory: &IDWriteFactory2,
    face: &IDWriteFontFace,
    glyph: u16,
    pixel_size: f32,
    origin_x: f32,
    origin_y: f32,
    rendering_mode: DWRITE_RENDERING_MODE,
    measuring_mode: DWRITE_MEASURING_MODE,
) -> Result<Option<GlyphMask>, String> {
    let advance = 0.0f32;
    let offset = DWRITE_GLYPH_OFFSET::default();
    let mut run = DWRITE_GLYPH_RUN {
        fontFace: ManuallyDrop::new(Some(face.clone())),
        fontEmSize: pixel_size,
        glyphCount: 1,
        glyphIndices: &glyph,
        glyphAdvances: &advance,
        glyphOffsets: &offset,
        isSideways: false.into(),
        bidiLevel: 0,
    };
    let transform = identity_matrix();
    let analysis_result = unsafe {
        factory.CreateGlyphRunAnalysis(
            &run,
            Some(&transform),
            rendering_mode,
            measuring_mode,
            DWRITE_GRID_FIT_MODE_ENABLED,
            DWRITE_TEXT_ANTIALIAS_MODE_GRAYSCALE,
            origin_x,
            origin_y,
        )
    };
    unsafe { ManuallyDrop::drop(&mut run.fontFace) };
    let analysis = analysis_result
        .map_err(|error| format!("cannot analyze DirectWrite glyph {glyph}: {error}"))?;
    let bounds = unsafe { analysis.GetAlphaTextureBounds(DWRITE_TEXTURE_ALIASED_1x1) }
        .map_err(|error| format!("cannot get DirectWrite glyph bounds: {error}"))?;
    if bounds.left >= bounds.right || bounds.top >= bounds.bottom {
        return Ok(None);
    }
    let size = (bounds.right - bounds.left) as usize * (bounds.bottom - bounds.top) as usize;
    let mut alpha = vec![0u8; size];
    unsafe { analysis.CreateAlphaTexture(DWRITE_TEXTURE_ALIASED_1x1, &bounds, &mut alpha) }
        .map_err(|error| format!("cannot rasterize DirectWrite glyph: {error}"))?;
    Ok(Some(GlyphMask { bounds, alpha }))
}

#[allow(clippy::too_many_arguments)]
fn composite_mask(
    target: &mut [u8],
    target_width: u32,
    texture_left: i32,
    texture_top: i32,
    mask: &GlyphMask,
    red: u8,
    green: u8,
    blue: u8,
    color_lut: Option<&[u8; 256]>,
) {
    let mask_width = (mask.bounds.right - mask.bounds.left) as usize;
    let mask_height = (mask.bounds.bottom - mask.bounds.top) as usize;
    for y in 0..mask_height {
        for x in 0..mask_width {
            let raw_alpha = mask.alpha[y * mask_width + x];
            let alpha = color_lut
                .map(|lut| lut[raw_alpha as usize])
                .unwrap_or_else(|| skia_coverage(raw_alpha)) as u32;
            if alpha == 0 {
                continue;
            }
            let target_x = (mask.bounds.left - texture_left) as usize + x;
            let target_y = (mask.bounds.top - texture_top) as usize + y;
            let destination = (target_y * target_width as usize + target_x) * 4;
            let inverse = 255 - alpha;
            target[destination] = (mul_div_255_round(blue as u32, alpha)
                + mul_div_255_round(target[destination] as u32, inverse))
                as u8;
            target[destination + 1] = (mul_div_255_round(green as u32, alpha)
                + mul_div_255_round(target[destination + 1] as u32, inverse))
                as u8;
            target[destination + 2] = (mul_div_255_round(red as u32, alpha)
                + mul_div_255_round(target[destination + 2] as u32, inverse))
                as u8;
            target[destination + 3] =
                (alpha + mul_div_255_round(target[destination + 3] as u32, inverse)) as u8;
        }
    }
}

fn mul_div_255_round(value: u32, alpha: u32) -> u32 {
    (value * alpha + 127) / 255
}

fn identity_matrix() -> DWRITE_MATRIX {
    DWRITE_MATRIX {
        m11: 1.0,
        m12: 0.0,
        m21: 0.0,
        m22: 1.0,
        dx: 0.0,
        dy: 0.0,
    }
}

fn skia_coverage(alpha: u8) -> u8 {
    const INPUT: [u8; 17] = [
        0, 16, 32, 48, 64, 80, 96, 112, 128, 143, 159, 175, 191, 207, 223, 239, 255,
    ];
    const OUTPUT: [u8; 17] = [
        0, 71, 99, 120, 137, 152, 165, 177, 188, 197, 207, 216, 224, 233, 240, 248, 255,
    ];
    let Some(upper) = INPUT.iter().position(|value| *value >= alpha) else {
        return 255;
    };
    if upper == 0 || INPUT[upper] == alpha {
        return OUTPUT[upper];
    }
    let lower = upper - 1;
    let span = (INPUT[upper] - INPUT[lower]) as u32;
    let position = (alpha - INPUT[lower]) as u32;
    let output_span = (OUTPUT[upper] - OUTPUT[lower]) as u32;
    (OUTPUT[lower] as u32 + (output_span * position + span / 2) / span) as u8
}

fn skia_srgb_coverage_lut(red: u8, green: u8, blue: u8) -> [u8; 256] {
    let luminance = (red as u32 * 54 + green as u32 * 183 + blue as u32 * 19) >> 8;
    let bucket = (luminance as u8) >> 5;
    let base = bucket << 5;
    let canonical = base | (base >> 3) | (base >> 6);
    let source = canonical as f32 / 255.0;
    let destination = 1.0 - source;
    let linear_source = srgb_to_linear(source);
    let linear_destination = srgb_to_linear(destination);
    let adjusted_contrast = (128.0 / 255.0) * linear_destination;
    let mut lut = [0u8; 256];
    for (index, output) in lut.iter_mut().enumerate() {
        let raw_alpha = index as f32 / 255.0;
        let source_alpha = raw_alpha + (1.0 - raw_alpha) * adjusted_contrast * raw_alpha;
        let value = if (source - destination).abs() < (1.0 / 256.0) {
            source_alpha
        } else {
            let destination_alpha = 1.0 - source_alpha;
            let linear_output =
                linear_source * source_alpha + destination_alpha * linear_destination;
            let encoded_output = linear_to_srgb(linear_output);
            (encoded_output - destination) / (source - destination)
        };
        *output = (value * 255.0).round().clamp(0.0, 255.0) as u8;
    }
    lut
}

fn srgb_to_linear(value: f32) -> f32 {
    if value <= 0.04045 {
        value / 12.92
    } else {
        ((value + 0.055) / 1.055).powf(2.4)
    }
}

fn linear_to_srgb(value: f32) -> f32 {
    if value <= 0.003_130_8 {
        value * 12.92
    } else {
        1.055 * value.powf(1.0 / 2.4) - 0.055
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn enumerated_font_families_can_be_selected() {
        let factory: IDWriteFactory =
            unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED) }.unwrap();
        let collection = system_font_collection(&factory).unwrap();
        let families = font_families().unwrap();
        assert!(!families.is_empty());
        for family in families {
            font_face(&collection, &family, false).unwrap();
            font_face(&collection, &family, true).unwrap();
        }
    }

    #[test]
    fn missing_font_family_uses_a_safe_fallback() {
        let factory: IDWriteFactory =
            unsafe { DWriteCreateFactory(DWRITE_FACTORY_TYPE_SHARED) }.unwrap();
        let request = TextRasterRequest {
            text: "fallback".to_owned(),
            font_family: "__missing_microblocks_qol_font__".to_owned(),
            font_file: String::new(),
            bold: false,
            pixel_size: 20,
            line_height: 24,
            red: 255,
            green: 255,
            blue: 255,
        };
        system_font_face(&factory, &request).unwrap();
    }
}

use std::fs;
use std::path::{Path, PathBuf};

use ffmpeg::{Packet, Rational, codec, encoder, format, frame, media, software};
use ffmpeg_next as ffmpeg;
use serde::Deserialize;
use thiserror::Error;

use crate::encoder::{
    encoder_candidates, encoder_options, even_dimension, pixel_format_for_encoder,
};
use crate::finalizer_audio;

#[derive(Debug, Deserialize)]
#[serde(default)]
pub struct FinalizePlan {
    pub clips: Vec<FinalizeClip>,
    pub output_path: String,
    pub encoder: String,
    pub bitrate_kbps: u32,
    pub fps: u32,
    pub reconstruct_bgm: bool,
    pub bgm_event_map_file: String,
}

impl Default for FinalizePlan {
    fn default() -> Self {
        Self {
            clips: Vec::new(),
            output_path: String::new(),
            encoder: "auto".to_owned(),
            bitrate_kbps: 12_000,
            fps: 60,
            reconstruct_bgm: false,
            bgm_event_map_file: String::new(),
        }
    }
}

#[derive(Debug, Deserialize)]
pub struct FinalizeClip {
    pub source: String,
    pub start_seconds: f64,
    pub duration_seconds: f64,
    #[serde(default)]
    pub music_event: String,
    #[serde(default)]
    pub music_timeline_milliseconds: i64,
}

#[derive(Debug, Error)]
pub enum FinalizeError {
    #[error("finalize plan has no clips")]
    Empty,
    #[error("all timeline clips must reference the same continuous recording")]
    MultipleSources,
    #[error("timeline clips must be ordered by nondecreasing source time")]
    OutOfOrder,
    #[error("invalid clip range start={start} duration={duration}")]
    Range { start: f64, duration: f64 },
    #[error("invalid finalizer frame rate {0}")]
    FrameRate(u32),
    #[error("invalid finalizer bitrate {0} kbps")]
    Bitrate(u32),
    #[error("output path has no parent: {0}")]
    MissingParent(PathBuf),
    #[error("cannot create output directory {path}: {source}")]
    CreateDirectory {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("cannot open input {path}: {source}")]
    OpenInput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("input has no video stream: {0}")]
    MissingVideo(PathBuf),
    #[error("cannot open video decoder: {0}")]
    Decoder(ffmpeg::Error),
    #[error("video decoder rejected a packet: {0}")]
    SendPacket(ffmpeg::Error),
    #[error("video decoder could not be flushed: {0}")]
    FlushDecoder(ffmpeg::Error),
    #[error("no decoded frame intersects the retained timeline")]
    NoFrames,
    #[error("no usable H.264 encoder was found; tried {0}")]
    NoEncoder(String),
    #[error("cannot create output {path}: {source}")]
    CreateOutput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("cannot add the {encoder} output stream: {source}")]
    AddStream {
        encoder: String,
        source: ffmpeg::Error,
    },
    #[error("cannot open the {encoder} output encoder: {source}")]
    OpenEncoder {
        encoder: String,
        source: ffmpeg::Error,
    },
    #[error("cannot write output header: {0}")]
    Header(ffmpeg::Error),
    #[error("cannot convert decoded frame: {0}")]
    Convert(ffmpeg::Error),
    #[error("output encoder rejected a frame: {0}")]
    SendFrame(ffmpeg::Error),
    #[error("cannot write output packet: {0}")]
    Packet(ffmpeg::Error),
    #[error("cannot flush output encoder: {0}")]
    FlushEncoder(ffmpeg::Error),
    #[error("cannot write output trailer: {0}")]
    Trailer(ffmpeg::Error),
    #[error("cannot replace final output {path}: {source}")]
    Replace {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("cannot finalize recording audio: {0}")]
    Audio(#[from] finalizer_audio::AudioFinalizeError),
}

pub fn finalize(plan: &FinalizePlan) -> Result<(), FinalizeError> {
    finalize_with_progress(plan, |_| {})
}

pub fn finalize_with_progress(
    plan: &FinalizePlan,
    mut report_progress: impl FnMut(f32),
) -> Result<(), FinalizeError> {
    validate_plan(plan)?;
    report_progress(0.0);
    ffmpeg::init().map_err(|source| FinalizeError::OpenInput {
        path: PathBuf::from("FFmpeg initialization"),
        source,
    })?;

    let output_path = Path::new(&plan.output_path);
    let parent = output_path
        .parent()
        .filter(|parent| !parent.as_os_str().is_empty())
        .ok_or_else(|| FinalizeError::MissingParent(output_path.to_owned()))?;
    fs::create_dir_all(parent).map_err(|source| FinalizeError::CreateDirectory {
        path: parent.to_owned(),
        source,
    })?;
    let video_temporary = working_path(output_path, "video", "mp4");
    let audio_temporary = working_path(output_path, "audio", "m4a");
    let mixed_pcm = working_path(output_path, "mix", "f32");
    let mux_temporary = temporary_output_path(output_path);
    for path in [
        &video_temporary,
        &audio_temporary,
        &mixed_pcm,
        &mux_temporary,
    ] {
        let _ = fs::remove_file(path);
    }

    let source_path = Path::new(&plan.clips[0].source);
    let mut input = format::input(source_path).map_err(|source| FinalizeError::OpenInput {
        path: source_path.to_owned(),
        source,
    })?;
    let video = input
        .streams()
        .best(media::Type::Video)
        .ok_or_else(|| FinalizeError::MissingVideo(source_path.to_owned()))?;
    let stream_index = video.index();
    let input_time_base = video.time_base();
    let mut decoder = codec::context::Context::from_parameters(video.parameters())
        .and_then(|context| context.decoder().video())
        .map_err(FinalizeError::Decoder)?;
    drop(video);

    let mut selection = TimelineSelection::new(&plan.clips);
    let total_duration = plan
        .clips
        .iter()
        .map(|clip| clip.duration_seconds)
        .sum::<f64>();
    let mut output = None;
    let mut decoded = frame::Video::empty();
    for (stream, packet) in input.packets() {
        if stream.index() != stream_index {
            continue;
        }
        decoder
            .send_packet(&packet)
            .map_err(FinalizeError::SendPacket)?;
        drain_decoder(
            &mut decoder,
            &mut decoded,
            input_time_base,
            plan,
            &video_temporary,
            &mut selection,
            &mut output,
            total_duration,
            &mut report_progress,
        )?;
        if selection.finished() {
            break;
        }
    }
    if !selection.finished() {
        decoder.send_eof().map_err(FinalizeError::FlushDecoder)?;
        drain_decoder(
            &mut decoder,
            &mut decoded,
            input_time_base,
            plan,
            &video_temporary,
            &mut selection,
            &mut output,
            total_duration,
            &mut report_progress,
        )?;
    }

    let mut output = output.ok_or(FinalizeError::NoFrames)?;
    output.finish()?;
    drop(output);
    report_progress(0.85);

    let sidecar = PathBuf::from(format!("{}.sfxchunks", source_path.display()));
    let bgm_map = (plan.reconstruct_bgm && !plan.bgm_event_map_file.trim().is_empty())
        .then(|| Path::new(plan.bgm_event_map_file.trim()));
    let has_audio = finalizer_audio::build_audio_track(
        &sidecar,
        &plan.clips,
        &mixed_pcm,
        &audio_temporary,
        bgm_map,
    )?;
    report_progress(0.96);
    if has_audio {
        finalizer_audio::mux_video_and_audio(&video_temporary, &audio_temporary, &mux_temporary)?;
    }
    report_progress(0.99);
    fs::remove_file(output_path).ok();
    let completed = if has_audio {
        &mux_temporary
    } else {
        &video_temporary
    };
    fs::rename(completed, output_path).map_err(|source| FinalizeError::Replace {
        path: output_path.to_owned(),
        source,
    })?;
    for path in [
        &video_temporary,
        &audio_temporary,
        &mixed_pcm,
        &mux_temporary,
    ] {
        let _ = fs::remove_file(path);
    }
    report_progress(1.0);
    Ok(())
}

fn validate_plan(plan: &FinalizePlan) -> Result<(), FinalizeError> {
    if plan.clips.is_empty() {
        return Err(FinalizeError::Empty);
    }
    if !(1..=240).contains(&plan.fps) {
        return Err(FinalizeError::FrameRate(plan.fps));
    }
    if !(100..=200_000).contains(&plan.bitrate_kbps) {
        return Err(FinalizeError::Bitrate(plan.bitrate_kbps));
    }
    let source = &plan.clips[0].source;
    let mut previous_start = -1.0;
    for clip in &plan.clips {
        if !clip.source.eq_ignore_ascii_case(source) {
            return Err(FinalizeError::MultipleSources);
        }
        if !clip.start_seconds.is_finite()
            || clip.start_seconds < 0.0
            || !clip.duration_seconds.is_finite()
            || clip.duration_seconds <= 0.0
        {
            return Err(FinalizeError::Range {
                start: clip.start_seconds,
                duration: clip.duration_seconds,
            });
        }
        if clip.start_seconds < previous_start {
            return Err(FinalizeError::OutOfOrder);
        }
        previous_start = clip.start_seconds;
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn drain_decoder(
    decoder: &mut ffmpeg::decoder::Video,
    decoded: &mut frame::Video,
    input_time_base: Rational,
    plan: &FinalizePlan,
    output_path: &Path,
    selection: &mut TimelineSelection<'_>,
    output: &mut Option<TimelineOutput>,
    total_duration: f64,
    report_progress: &mut impl FnMut(f32),
) -> Result<(), FinalizeError> {
    while decoder.receive_frame(decoded).is_ok() {
        let Some(timestamp) = decoded.timestamp() else {
            continue;
        };
        let source_seconds = timestamp as f64 * f64::from(input_time_base);
        let Some(output_seconds) = selection.map(source_seconds) else {
            continue;
        };
        if output.is_none() {
            *output = Some(TimelineOutput::create(plan, output_path, decoded)?);
        }
        output
            .as_mut()
            .expect("output initialized above")
            .encode(decoded, output_seconds)?;
        report_progress(((output_seconds / total_duration) * 0.85).clamp(0.0, 0.85) as f32);
    }
    Ok(())
}

struct TimelineSelection<'a> {
    clips: &'a [FinalizeClip],
    index: usize,
    output_offset: f64,
}

impl<'a> TimelineSelection<'a> {
    fn new(clips: &'a [FinalizeClip]) -> Self {
        Self {
            clips,
            index: 0,
            output_offset: 0.0,
        }
    }

    fn map(&mut self, source_seconds: f64) -> Option<f64> {
        while let Some(clip) = self.clips.get(self.index) {
            let end = clip.start_seconds + clip.duration_seconds;
            if source_seconds < end {
                break;
            }
            self.output_offset += clip.duration_seconds;
            self.index += 1;
        }
        let clip = self.clips.get(self.index)?;
        if source_seconds < clip.start_seconds {
            return None;
        }
        Some(self.output_offset + source_seconds - clip.start_seconds)
    }

    fn finished(&self) -> bool {
        self.index >= self.clips.len()
    }
}

struct TimelineOutput {
    output: format::context::Output,
    encoder: encoder::video::Encoder,
    scaler: software::scaling::Context,
    converted: frame::Video,
    output_width: u32,
    output_height: u32,
    input_format: ffmpeg::format::Pixel,
    input_width: u32,
    input_height: u32,
    pixel_format: ffmpeg::format::Pixel,
    stream_index: usize,
    encoder_time_base: Rational,
    stream_time_base: Rational,
    fps: u32,
    last_pts: i64,
    finished: bool,
}

impl TimelineOutput {
    fn create(
        plan: &FinalizePlan,
        path: &Path,
        first: &frame::Video,
    ) -> Result<Self, FinalizeError> {
        let mut failures = Vec::new();
        for name in encoder_candidates(&plan.encoder) {
            let _ = fs::remove_file(path);
            match Self::try_create(plan, path, first, name) {
                Ok(output) => return Ok(output),
                Err(error) => failures.push(format!("{name}: {error}")),
            }
        }
        Err(FinalizeError::NoEncoder(failures.join("; ")))
    }

    fn try_create(
        plan: &FinalizePlan,
        path: &Path,
        first: &frame::Video,
        encoder_name: &str,
    ) -> Result<Self, FinalizeError> {
        let codec = encoder::find_by_name(encoder_name).ok_or_else(|| {
            FinalizeError::NoEncoder(format!("{encoder_name} is absent from this FFmpeg build"))
        })?;
        let pixel_format = pixel_format_for_encoder(encoder_name);
        let output_width = even_dimension(first.width());
        let output_height = even_dimension(first.height());
        let encoder_time_base = Rational(1, plan.fps as i32);
        let mut output = format::output(path).map_err(|source| FinalizeError::CreateOutput {
            path: path.to_owned(),
            source,
        })?;
        let global_header = output
            .format()
            .flags()
            .contains(format::Flags::GLOBAL_HEADER);
        let mut video = codec::context::Context::new_with_codec(codec)
            .encoder()
            .video()
            .map_err(|source| FinalizeError::OpenEncoder {
                encoder: encoder_name.to_owned(),
                source,
            })?;
        video.set_width(output_width);
        video.set_height(output_height);
        video.set_format(pixel_format);
        video.set_time_base(encoder_time_base);
        video.set_frame_rate(Some(Rational(plan.fps as i32, 1)));
        video.set_bit_rate(plan.bitrate_kbps as usize * 1_000);
        video.set_gop(plan.fps.saturating_mul(2));
        video.set_max_b_frames(0);
        if global_header {
            video.set_flags(codec::Flags::GLOBAL_HEADER);
        }
        let opened = video
            .open_as_with(codec, encoder_options(encoder_name))
            .map_err(|source| FinalizeError::OpenEncoder {
                encoder: encoder_name.to_owned(),
                source,
            })?;
        let stream_index;
        {
            let mut stream =
                output
                    .add_stream(codec)
                    .map_err(|source| FinalizeError::AddStream {
                        encoder: encoder_name.to_owned(),
                        source,
                    })?;
            stream.set_time_base(encoder_time_base);
            stream.set_avg_frame_rate(Rational(plan.fps as i32, 1));
            stream.set_parameters(&opened);
            stream_index = stream.index();
        }
        output.write_header().map_err(FinalizeError::Header)?;
        let stream_time_base = output
            .stream(stream_index)
            .expect("newly-added output stream disappeared")
            .time_base();
        let scaler = software::scaling::Context::get(
            first.format(),
            first.width(),
            first.height(),
            pixel_format,
            output_width,
            output_height,
            software::scaling::Flags::BILINEAR,
        )
        .map_err(FinalizeError::Convert)?;
        Ok(Self {
            output,
            encoder: opened,
            scaler,
            converted: frame::Video::new(pixel_format, output_width, output_height),
            output_width,
            output_height,
            input_format: first.format(),
            input_width: first.width(),
            input_height: first.height(),
            pixel_format,
            stream_index,
            encoder_time_base,
            stream_time_base,
            fps: plan.fps,
            last_pts: -1,
            finished: false,
        })
    }

    fn encode(&mut self, source: &frame::Video, output_seconds: f64) -> Result<(), FinalizeError> {
        if source.format() != self.input_format
            || source.width() != self.input_width
            || source.height() != self.input_height
        {
            self.input_format = source.format();
            self.input_width = source.width();
            self.input_height = source.height();
            self.scaler.cached(
                source.format(),
                source.width(),
                source.height(),
                self.pixel_format,
                self.output_width,
                self.output_height,
                software::scaling::Flags::BILINEAR,
            );
        }
        self.scaler
            .run(source, &mut self.converted)
            .map_err(FinalizeError::Convert)?;
        let timestamp = (output_seconds * self.fps as f64).round().max(0.0) as i64;
        if timestamp <= self.last_pts {
            return Ok(());
        }
        self.last_pts = timestamp;
        self.converted.set_pts(Some(timestamp));
        self.encoder
            .send_frame(&self.converted)
            .map_err(FinalizeError::SendFrame)?;
        self.write_available_packets()
    }

    fn finish(&mut self) -> Result<(), FinalizeError> {
        if self.finished {
            return Ok(());
        }
        self.encoder
            .send_eof()
            .map_err(FinalizeError::FlushEncoder)?;
        self.write_available_packets()?;
        self.output
            .write_trailer()
            .map_err(FinalizeError::Trailer)?;
        self.finished = true;
        Ok(())
    }

    fn write_available_packets(&mut self) -> Result<(), FinalizeError> {
        let mut packet = Packet::empty();
        loop {
            match self.encoder.receive_packet(&mut packet) {
                Ok(()) => {
                    packet.set_stream(self.stream_index);
                    packet.set_position(-1);
                    packet.rescale_ts(self.encoder_time_base, self.stream_time_base);
                    packet
                        .write_interleaved(&mut self.output)
                        .map_err(FinalizeError::Packet)?;
                }
                Err(ffmpeg::Error::Other { errno }) if errno == ffmpeg::error::EAGAIN => break,
                Err(ffmpeg::Error::Eof) => break,
                Err(error) => return Err(FinalizeError::Packet(error)),
            }
        }
        Ok(())
    }
}

impl Drop for TimelineOutput {
    fn drop(&mut self) {
        let _ = self.finish();
    }
}

fn temporary_output_path(output: &Path) -> PathBuf {
    let extension = output
        .extension()
        .and_then(|value| value.to_str())
        .unwrap_or("mkv");
    output.with_extension(format!("working.{extension}"))
}

fn working_path(output: &Path, purpose: &str, extension: &str) -> PathBuf {
    let stem = output
        .file_stem()
        .and_then(|value| value.to_str())
        .unwrap_or("recording");
    output.with_file_name(format!("{stem}.working.{purpose}.{extension}"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::encoder::VideoFileEncoder;
    use crate::{AudioChunk, CaptureConfig, CapturedFrame, write_audio_chunk};
    use std::fs::File;
    use std::io::{BufWriter, Write};
    use std::time::{SystemTime, UNIX_EPOCH};

    #[test]
    fn timeline_selection_concatenates_disjoint_ranges() {
        let clips = vec![
            FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 1.0,
                duration_seconds: 2.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
            },
            FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 5.0,
                duration_seconds: 1.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
            },
        ];
        let mut selection = TimelineSelection::new(&clips);
        assert_eq!(selection.map(0.5), None);
        assert_eq!(selection.map(1.5), Some(0.5));
        assert_eq!(selection.map(4.0), None);
        assert_eq!(selection.map(5.25), Some(2.25));
        assert_eq!(selection.map(6.0), None);
        assert!(selection.finished());
    }

    #[test]
    fn transcodes_arbitrary_ranges_from_one_continuous_source_when_enabled() {
        if std::env::var_os("MQOL_TEST_FFMPEG").is_none() {
            return;
        }
        let directory = tempfile::tempdir().unwrap();
        let source = directory.path().join("continuous.mkv");
        write_continuous_source(&source);
        write_continuous_audio(&source);
        let output = directory.path().join("joined.mp4");
        finalize(&FinalizePlan {
            clips: vec![
                FinalizeClip {
                    source: source.to_string_lossy().into_owned(),
                    start_seconds: 0.25,
                    duration_seconds: 0.5,
                    music_event: String::new(),
                    music_timeline_milliseconds: 0,
                },
                FinalizeClip {
                    source: source.to_string_lossy().into_owned(),
                    start_seconds: 1.5,
                    duration_seconds: 0.5,
                    music_event: String::new(),
                    music_timeline_milliseconds: 0,
                },
            ],
            output_path: output.to_string_lossy().into_owned(),
            encoder: "libopenh264".to_owned(),
            bitrate_kbps: 1_000,
            fps: 30,
            ..FinalizePlan::default()
        })
        .unwrap();
        assert!(fs::metadata(&output).unwrap().len() > 1_000);
        let mut joined = format::input(&output).unwrap();
        let video_stream = joined.streams().best(media::Type::Video).unwrap();
        let stream_index = video_stream.index();
        let time_base = video_stream.time_base();
        drop(video_stream);
        let audio_stream = joined.streams().best(media::Type::Audio).unwrap();
        assert_eq!(audio_stream.parameters().id(), codec::Id::AAC);
        drop(audio_stream);
        let mut last_timestamp = 0_i64;
        for (stream, packet) in joined.packets() {
            if stream.index() == stream_index {
                last_timestamp = last_timestamp.max(packet.pts().unwrap_or(0));
            }
        }
        assert!(last_timestamp as f64 * f64::from(time_base) > 0.8);
    }

    fn write_continuous_source(path: &Path) {
        let config = CaptureConfig {
            output_path: Some(path.to_string_lossy().into_owned()),
            encoder: "libopenh264".to_owned(),
            fps: 30,
            bitrate_kbps: 1_000,
            ..CaptureConfig::default()
        };
        let start = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .unwrap()
            .as_nanos() as u64;
        let mut frame = CapturedFrame {
            width: 64,
            height: 64,
            captured_at_unix_nanos: start,
            bgra: vec![0; 64 * 64 * 4],
        };
        let mut encoder = VideoFileEncoder::create(&config, &frame).unwrap();
        for index in 0..90_u64 {
            frame.captured_at_unix_nanos = start + index * 1_000_000_000 / 30;
            for pixel in frame.bgra.chunks_exact_mut(4) {
                pixel[0] = (index * 3) as u8;
                pixel[1] = (index * 5) as u8;
                pixel[2] = (index * 7) as u8;
                pixel[3] = 255;
            }
            encoder.encode(&frame).unwrap();
        }
        encoder.finish().unwrap();
    }

    fn write_continuous_audio(video_path: &Path) {
        let path = PathBuf::from(format!("{}.sfxchunks", video_path.display()));
        let mut writer = BufWriter::new(File::create(path).unwrap());
        writer.write_all(b"MQOLAUD1").unwrap();
        let sample_rate = 48_000_u32;
        let channels = 2_u16;
        let frames_per_chunk = 1_024_usize;
        for chunk_index in 0..141_u64 {
            let media_time_nanos =
                chunk_index * frames_per_chunk as u64 * 1_000_000_000 / u64::from(sample_rate);
            for (bus_id, frequency, amplitude) in [(1_u16, 440.0_f32, 0.20_f32), (2, 660.0, 0.10)] {
                let mut samples = Vec::with_capacity(frames_per_chunk * usize::from(channels));
                for local_frame in 0..frames_per_chunk {
                    let absolute_frame = chunk_index as usize * frames_per_chunk + local_frame;
                    let value = (absolute_frame as f32 * frequency * std::f32::consts::TAU
                        / sample_rate as f32)
                        .sin()
                        * amplitude;
                    samples.extend_from_slice(&[value, value]);
                }
                write_audio_chunk(
                    &mut writer,
                    &AudioChunk {
                        media_time_nanos,
                        sample_rate,
                        channels,
                        bus_id,
                        samples,
                    },
                )
                .unwrap();
            }
        }
        writer.flush().unwrap();
    }
}

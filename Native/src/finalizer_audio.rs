use std::collections::HashMap;
use std::fs::{self, File, OpenOptions};
use std::io::{BufReader, Read, Seek, SeekFrom, Write};
use std::path::{Path, PathBuf};

use ffmpeg::{ChannelLayout, Packet, Rational, codec, encoder, format, frame, media, software};
use ffmpeg_next as ffmpeg;
use thiserror::Error;

use crate::finalizer::FinalizeClip;

const SIDECAR_MAGIC: &[u8; 8] = b"MQOLAUD1";
const CHUNK_HEADER_BYTES: usize = 24;
const MAX_CHUNK_SAMPLES: usize = 16_384;
const AUDIO_BITRATE: usize = 192_000;
const DEFAULT_SAMPLE_RATE: u32 = 48_000;
const DEFAULT_CHANNELS: u16 = 2;

#[derive(Debug, Error)]
pub enum AudioFinalizeError {
    #[error("cannot access audio sidecar {path}: {source}")]
    Io {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("invalid audio sidecar {path}: {detail}")]
    InvalidSidecar { path: PathBuf, detail: String },
    #[error("unsupported captured audio layout: {channels} channels")]
    Channels { channels: u16 },
    #[error("audio timeline is too large")]
    TimelineTooLarge,
    #[error("cannot read BGM event map {path}: {source}")]
    ReadBgmMap {
        path: PathBuf,
        source: std::io::Error,
    },
    #[error("invalid BGM event map {path}: {source}")]
    ParseBgmMap {
        path: PathBuf,
        source: serde_json::Error,
    },
    #[error("cannot open mapped BGM {path}: {source}")]
    OpenBgm {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("mapped BGM has no audio stream: {0}")]
    MissingBgmAudio(PathBuf),
    #[error("cannot decode mapped BGM: {0}")]
    DecodeBgm(ffmpeg::Error),
    #[error("cannot resample mapped BGM: {0}")]
    ResampleBgm(ffmpeg::Error),
    #[error("FFmpeg AAC encoder is unavailable")]
    MissingAac,
    #[error("AAC encoder does not support planar f32 samples")]
    UnsupportedAacFormat,
    #[error("cannot create audio output {path}: {source}")]
    CreateAudioOutput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("cannot configure AAC output: {0}")]
    ConfigureAac(ffmpeg::Error),
    #[error("AAC encoder rejected an audio frame: {0}")]
    SendAudio(ffmpeg::Error),
    #[error("cannot write AAC packet: {0}")]
    AudioPacket(ffmpeg::Error),
    #[error("cannot write AAC trailer: {0}")]
    AudioTrailer(ffmpeg::Error),
    #[error("cannot open mux input {path}: {source}")]
    OpenMuxInput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("mux input {path} has no {kind} stream")]
    MissingMuxStream { path: PathBuf, kind: &'static str },
    #[error("cannot create mux output {path}: {source}")]
    CreateMuxOutput {
        path: PathBuf,
        source: ffmpeg::Error,
    },
    #[error("cannot configure mux output: {0}")]
    ConfigureMux(ffmpeg::Error),
    #[error("cannot write muxed packet: {0}")]
    MuxPacket(ffmpeg::Error),
    #[error("cannot write mux trailer: {0}")]
    MuxTrailer(ffmpeg::Error),
}

#[derive(Debug, Clone, Copy)]
struct AudioSpec {
    sample_rate: u32,
    channels: u16,
    total_frames: u64,
}

#[derive(Debug)]
struct SidecarChunk {
    media_time_nanos: u64,
    sample_rate: u32,
    channels: u16,
    bus_id: u16,
    samples: Vec<f32>,
}

pub fn build_audio_track(
    sidecar: &Path,
    clips: &[FinalizeClip],
    mixed_pcm: &Path,
    audio_output: &Path,
    bgm_event_map_file: Option<&Path>,
) -> Result<bool, AudioFinalizeError> {
    let bgm_map = bgm_event_map_file
        .map(load_bgm_map)
        .transpose()?
        .unwrap_or_default();
    let has_mapped_bgm = clips
        .iter()
        .any(|clip| bgm_map.contains_key(&clip.music_event));
    let captured_spec = sidecar
        .exists()
        .then(|| render_mix(sidecar, clips, mixed_pcm))
        .transpose()?
        .flatten();
    if captured_spec.is_none() && !has_mapped_bgm {
        return Ok(false);
    }
    let spec = captured_spec.unwrap_or(AudioSpec {
        sample_rate: DEFAULT_SAMPLE_RATE,
        channels: DEFAULT_CHANNELS,
        total_frames: total_timeline_frames(clips, DEFAULT_SAMPLE_RATE)?,
    });
    if captured_spec.is_none() {
        create_empty_mix(mixed_pcm, spec)?;
    }
    if has_mapped_bgm {
        mix_bgm_tracks(mixed_pcm, clips, &bgm_map, spec)?;
    }
    encode_aac(mixed_pcm, audio_output, spec)?;
    Ok(true)
}

fn render_mix(
    sidecar: &Path,
    clips: &[FinalizeClip],
    mixed_pcm: &Path,
) -> Result<Option<AudioSpec>, AudioFinalizeError> {
    let file = File::open(sidecar).map_err(|source| io_error(sidecar, source))?;
    let mut reader = BufReader::new(file);
    let mut magic = [0_u8; 8];
    reader
        .read_exact(&mut magic)
        .map_err(|source| io_error(sidecar, source))?;
    if &magic != SIDECAR_MAGIC {
        return Err(invalid(sidecar, "bad magic"));
    }
    let Some(first) = read_chunk(&mut reader, sidecar)? else {
        return Ok(None);
    };
    if !(1..=2).contains(&first.channels) {
        return Err(AudioFinalizeError::Channels {
            channels: first.channels,
        });
    }
    let total_frames = clips.iter().try_fold(0_u64, |total, clip| {
        let frames = seconds_to_frames(clip.duration_seconds, first.sample_rate)?;
        total
            .checked_add(frames)
            .ok_or(AudioFinalizeError::TimelineTooLarge)
    })?;
    let total_bytes = total_frames
        .checked_mul(u64::from(first.channels))
        .and_then(|samples| samples.checked_mul(4))
        .ok_or(AudioFinalizeError::TimelineTooLarge)?;
    let mut mixed = OpenOptions::new()
        .create(true)
        .truncate(true)
        .read(true)
        .write(true)
        .open(mixed_pcm)
        .map_err(|source| io_error(mixed_pcm, source))?;
    mixed
        .set_len(total_bytes)
        .map_err(|source| io_error(mixed_pcm, source))?;

    mix_chunk(
        &mut mixed,
        &first,
        clips,
        first.sample_rate,
        first.channels,
        sidecar,
    )?;
    while let Some(chunk) = read_chunk(&mut reader, sidecar)? {
        if chunk.sample_rate != first.sample_rate || chunk.channels != first.channels {
            return Err(invalid(
                sidecar,
                format!(
                    "audio format changed from {} Hz/{} ch to {} Hz/{} ch",
                    first.sample_rate, first.channels, chunk.sample_rate, chunk.channels
                ),
            ));
        }
        mix_chunk(
            &mut mixed,
            &chunk,
            clips,
            first.sample_rate,
            first.channels,
            sidecar,
        )?;
    }
    mixed
        .flush()
        .map_err(|source| io_error(mixed_pcm, source))?;
    Ok(Some(AudioSpec {
        sample_rate: first.sample_rate,
        channels: first.channels,
        total_frames,
    }))
}

fn total_timeline_frames(
    clips: &[FinalizeClip],
    sample_rate: u32,
) -> Result<u64, AudioFinalizeError> {
    clips.iter().try_fold(0_u64, |total, clip| {
        total
            .checked_add(seconds_to_frames(clip.duration_seconds, sample_rate)?)
            .ok_or(AudioFinalizeError::TimelineTooLarge)
    })
}

fn create_empty_mix(path: &Path, spec: AudioSpec) -> Result<(), AudioFinalizeError> {
    let total_bytes = spec
        .total_frames
        .checked_mul(u64::from(spec.channels))
        .and_then(|samples| samples.checked_mul(4))
        .ok_or(AudioFinalizeError::TimelineTooLarge)?;
    let file = OpenOptions::new()
        .create(true)
        .truncate(true)
        .read(true)
        .write(true)
        .open(path)
        .map_err(|source| io_error(path, source))?;
    file.set_len(total_bytes)
        .map_err(|source| io_error(path, source))
}

fn load_bgm_map(path: &Path) -> Result<HashMap<String, PathBuf>, AudioFinalizeError> {
    let bytes = fs::read(path).map_err(|source| AudioFinalizeError::ReadBgmMap {
        path: path.to_owned(),
        source,
    })?;
    let configured: HashMap<String, String> =
        serde_json::from_slice(&bytes).map_err(|source| AudioFinalizeError::ParseBgmMap {
            path: path.to_owned(),
            source,
        })?;
    let base = path.parent().unwrap_or_else(|| Path::new("."));
    Ok(configured
        .into_iter()
        .filter_map(|(event, configured_path)| {
            let configured_path = configured_path.trim();
            if event.trim().is_empty() || configured_path.is_empty() {
                return None;
            }
            let audio = PathBuf::from(configured_path);
            Some((
                event,
                if audio.is_absolute() {
                    audio
                } else {
                    base.join(audio)
                },
            ))
        })
        .collect())
}

fn mix_bgm_tracks(
    mixed_pcm: &Path,
    clips: &[FinalizeClip],
    event_map: &HashMap<String, PathBuf>,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let mut mixed = OpenOptions::new()
        .read(true)
        .write(true)
        .open(mixed_pcm)
        .map_err(|source| io_error(mixed_pcm, source))?;
    let mut output_offset = 0_u64;
    for clip in clips {
        let clip_frames = seconds_to_frames(clip.duration_seconds, spec.sample_rate)?;
        if let Some(path) = event_map.get(&clip.music_event) {
            mix_bgm_segment(
                &mut mixed,
                path,
                clip.music_timeline_milliseconds.max(0) as f64 / 1_000.0,
                clip.duration_seconds,
                output_offset,
                spec,
            )?;
        }
        output_offset = output_offset
            .checked_add(clip_frames)
            .ok_or(AudioFinalizeError::TimelineTooLarge)?;
    }
    mixed.flush().map_err(|source| io_error(mixed_pcm, source))
}

fn mix_bgm_segment(
    mixed: &mut File,
    path: &Path,
    source_start_seconds: f64,
    duration_seconds: f64,
    output_offset_frames: u64,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let mut input = format::input(path).map_err(|source| AudioFinalizeError::OpenBgm {
        path: path.to_owned(),
        source,
    })?;
    let stream = input
        .streams()
        .best(media::Type::Audio)
        .ok_or_else(|| AudioFinalizeError::MissingBgmAudio(path.to_owned()))?;
    let stream_index = stream.index();
    let input_time_base = stream.time_base();
    let mut decoder = codec::context::Context::from_parameters(stream.parameters())
        .and_then(|context| context.decoder().audio())
        .map_err(AudioFinalizeError::DecodeBgm)?;
    drop(stream);

    let input_layout = if decoder.channel_layout().is_empty() {
        ChannelLayout::default(i32::from(decoder.channels()))
    } else {
        decoder.channel_layout()
    };
    let output_layout = match spec.channels {
        1 => ChannelLayout::MONO,
        2 => ChannelLayout::STEREO,
        channels => return Err(AudioFinalizeError::Channels { channels }),
    };
    let output_format = ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Packed);
    let mut resampler = software::resampling::Context::get(
        decoder.format(),
        input_layout,
        decoder.rate(),
        output_format,
        output_layout,
        spec.sample_rate,
    )
    .map_err(AudioFinalizeError::ResampleBgm)?;
    let seek_timestamp = (source_start_seconds * 1_000_000.0).round() as i64;
    input
        .seek(seek_timestamp, ..seek_timestamp)
        .map_err(AudioFinalizeError::DecodeBgm)?;
    decoder.flush();

    let source_end_seconds = source_start_seconds + duration_seconds;
    let mut decoded = frame::Audio::empty();
    let mut fallback_seconds = source_start_seconds;
    let mut finished = false;
    for (packet_stream, packet) in input.packets() {
        if packet_stream.index() != stream_index {
            continue;
        }
        decoder
            .send_packet(&packet)
            .map_err(AudioFinalizeError::DecodeBgm)?;
        if drain_bgm_decoder(
            &mut decoder,
            &mut decoded,
            &mut resampler,
            mixed,
            input_time_base,
            source_start_seconds,
            source_end_seconds,
            output_offset_frames,
            spec,
            &mut fallback_seconds,
        )? {
            finished = true;
            break;
        }
    }
    if !finished {
        decoder.send_eof().map_err(AudioFinalizeError::DecodeBgm)?;
        let _ = drain_bgm_decoder(
            &mut decoder,
            &mut decoded,
            &mut resampler,
            mixed,
            input_time_base,
            source_start_seconds,
            source_end_seconds,
            output_offset_frames,
            spec,
            &mut fallback_seconds,
        )?;
    }
    Ok(())
}

#[allow(clippy::too_many_arguments)]
fn drain_bgm_decoder(
    decoder: &mut ffmpeg::decoder::Audio,
    decoded: &mut frame::Audio,
    resampler: &mut software::resampling::Context,
    mixed: &mut File,
    input_time_base: Rational,
    source_start_seconds: f64,
    source_end_seconds: f64,
    output_offset_frames: u64,
    spec: AudioSpec,
    fallback_seconds: &mut f64,
) -> Result<bool, AudioFinalizeError> {
    while decoder.receive_frame(decoded).is_ok() {
        let frame_start_seconds = decoded
            .timestamp()
            .map(|timestamp| timestamp as f64 * f64::from(input_time_base))
            .unwrap_or(*fallback_seconds);
        let capacity = ((decoded.samples() as u64 * u64::from(spec.sample_rate)
            / u64::from(decoded.rate().max(1)))
            + 256) as usize;
        let output_layout = match spec.channels {
            1 => ChannelLayout::MONO,
            2 => ChannelLayout::STEREO,
            channels => return Err(AudioFinalizeError::Channels { channels }),
        };
        let mut converted = frame::Audio::new(
            ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Packed),
            capacity.max(1),
            output_layout,
        );
        converted.set_rate(spec.sample_rate);
        resampler
            .run(decoded, &mut converted)
            .map_err(AudioFinalizeError::ResampleBgm)?;
        let converted_frames = converted.samples();
        let frame_end_seconds =
            frame_start_seconds + converted_frames as f64 / f64::from(spec.sample_rate);
        *fallback_seconds = frame_end_seconds;
        if frame_start_seconds >= source_end_seconds {
            return Ok(true);
        }
        let overlap_start = frame_start_seconds.max(source_start_seconds);
        let overlap_end = frame_end_seconds.min(source_end_seconds);
        if overlap_start < overlap_end {
            let source_frame =
                seconds_to_frames(overlap_start - frame_start_seconds, spec.sample_rate)? as usize;
            let mut frames =
                seconds_to_frames(overlap_end - overlap_start, spec.sample_rate)? as usize;
            frames = frames.min(converted_frames.saturating_sub(source_frame));
            let destination_frame = output_offset_frames
                .checked_add(seconds_to_frames(
                    overlap_start - source_start_seconds,
                    spec.sample_rate,
                )?)
                .ok_or(AudioFinalizeError::TimelineTooLarge)?;
            let channels = usize::from(spec.channels);
            let samples = converted.plane::<f32>(0);
            add_samples(
                mixed,
                destination_frame,
                &samples[source_frame * channels..(source_frame + frames) * channels],
                spec.channels,
            )?;
        }
    }
    Ok(false)
}

fn mix_chunk(
    mixed: &mut File,
    chunk: &SidecarChunk,
    clips: &[FinalizeClip],
    sample_rate: u32,
    channels: u16,
    sidecar: &Path,
) -> Result<(), AudioFinalizeError> {
    if !(1..=2).contains(&chunk.bus_id) {
        return Err(invalid(sidecar, format!("unknown bus id {}", chunk.bus_id)));
    }
    let channel_count = usize::from(channels);
    let chunk_frames = chunk.samples.len() / channel_count;
    let chunk_start = nanos_to_frames(chunk.media_time_nanos, sample_rate);
    let chunk_end = chunk_start.saturating_add(chunk_frames as u64);
    let mut output_offset = 0_u64;
    for clip in clips {
        let clip_frames = seconds_to_frames(clip.duration_seconds, sample_rate)?;
        let clip_start = seconds_to_frames(clip.start_seconds, sample_rate)?;
        let clip_end = clip_start.saturating_add(clip_frames);
        let overlap_start = chunk_start.max(clip_start);
        let overlap_end = chunk_end.min(clip_end);
        if overlap_start < overlap_end {
            let source_frame = (overlap_start - chunk_start) as usize;
            let destination_frame = output_offset + overlap_start - clip_start;
            let frames = (overlap_end - overlap_start) as usize;
            add_samples(
                mixed,
                destination_frame,
                &chunk.samples
                    [source_frame * channel_count..(source_frame + frames) * channel_count],
                channels,
            )?;
        }
        output_offset = output_offset
            .checked_add(clip_frames)
            .ok_or(AudioFinalizeError::TimelineTooLarge)?;
    }
    Ok(())
}

fn add_samples(
    mixed: &mut File,
    destination_frame: u64,
    samples: &[f32],
    channels: u16,
) -> Result<(), AudioFinalizeError> {
    let byte_offset = destination_frame
        .checked_mul(u64::from(channels))
        .and_then(|value| value.checked_mul(4))
        .ok_or(AudioFinalizeError::TimelineTooLarge)?;
    let mut bytes = vec![0_u8; samples.len() * 4];
    mixed
        .seek(SeekFrom::Start(byte_offset))
        .and_then(|_| mixed.read_exact(&mut bytes))
        .map_err(|source| AudioFinalizeError::Io {
            path: PathBuf::from("mixed PCM workspace"),
            source,
        })?;
    for (index, sample) in samples.iter().enumerate() {
        let offset = index * 4;
        let existing = f32::from_le_bytes(bytes[offset..offset + 4].try_into().unwrap());
        bytes[offset..offset + 4]
            .copy_from_slice(&(existing + sample).clamp(-1.0, 1.0).to_le_bytes());
    }
    mixed
        .seek(SeekFrom::Start(byte_offset))
        .and_then(|_| mixed.write_all(&bytes))
        .map_err(|source| AudioFinalizeError::Io {
            path: PathBuf::from("mixed PCM workspace"),
            source,
        })
}

fn read_chunk(
    reader: &mut BufReader<File>,
    path: &Path,
) -> Result<Option<SidecarChunk>, AudioFinalizeError> {
    let mut header = [0_u8; CHUNK_HEADER_BYTES];
    match reader.read(&mut header[..1]) {
        Ok(0) => return Ok(None),
        Ok(1) => {}
        Ok(_) => unreachable!(),
        Err(source) => return Err(io_error(path, source)),
    }
    reader
        .read_exact(&mut header[1..])
        .map_err(|source| io_error(path, source))?;
    let media_time_nanos = u64::from_le_bytes(header[0..8].try_into().unwrap());
    let sample_rate = u32::from_le_bytes(header[8..12].try_into().unwrap());
    let channels = u16::from_le_bytes(header[12..14].try_into().unwrap());
    let bus_id = u16::from_le_bytes(header[14..16].try_into().unwrap());
    let frames = u32::from_le_bytes(header[16..20].try_into().unwrap()) as usize;
    let sample_count = u32::from_le_bytes(header[20..24].try_into().unwrap()) as usize;
    if !(8_000..=384_000).contains(&sample_rate)
        || channels == 0
        || sample_count == 0
        || sample_count > MAX_CHUNK_SAMPLES
        || sample_count % usize::from(channels) != 0
        || frames != sample_count / usize::from(channels)
    {
        return Err(invalid(path, "invalid chunk dimensions"));
    }
    let mut bytes = vec![0_u8; sample_count * 4];
    reader
        .read_exact(&mut bytes)
        .map_err(|source| io_error(path, source))?;
    let samples = bytes
        .chunks_exact(4)
        .map(|value| f32::from_le_bytes(value.try_into().unwrap()))
        .collect();
    Ok(Some(SidecarChunk {
        media_time_nanos,
        sample_rate,
        channels,
        bus_id,
        samples,
    }))
}

fn encode_aac(
    mixed_pcm: &Path,
    output_path: &Path,
    spec: AudioSpec,
) -> Result<(), AudioFinalizeError> {
    let codec = encoder::find(codec::Id::AAC)
        .ok_or(AudioFinalizeError::MissingAac)?
        .audio()
        .map_err(AudioFinalizeError::ConfigureAac)?;
    let sample_format = ffmpeg::format::Sample::F32(ffmpeg::format::sample::Type::Planar);
    if codec
        .formats()
        .is_some_and(|mut formats| !formats.any(|format| format == sample_format))
    {
        return Err(AudioFinalizeError::UnsupportedAacFormat);
    }
    let layout = match spec.channels {
        1 => ChannelLayout::MONO,
        2 => ChannelLayout::STEREO,
        channels => return Err(AudioFinalizeError::Channels { channels }),
    };
    let _ = fs::remove_file(output_path);
    let mut output =
        format::output(output_path).map_err(|source| AudioFinalizeError::CreateAudioOutput {
            path: output_path.to_owned(),
            source,
        })?;
    let global_header = output
        .format()
        .flags()
        .contains(format::Flags::GLOBAL_HEADER);
    let mut audio = codec::context::Context::new_with_codec(*codec)
        .encoder()
        .audio()
        .map_err(AudioFinalizeError::ConfigureAac)?;
    audio.set_rate(spec.sample_rate as i32);
    audio.set_channel_layout(layout);
    audio.set_format(sample_format);
    audio.set_time_base(Rational(1, spec.sample_rate as i32));
    audio.set_bit_rate(AUDIO_BITRATE);
    if global_header {
        audio.set_flags(codec::Flags::GLOBAL_HEADER);
    }
    let mut opened = audio
        .open_as(codec)
        .map_err(AudioFinalizeError::ConfigureAac)?;
    let stream_index;
    {
        let mut stream = output
            .add_stream(codec)
            .map_err(AudioFinalizeError::ConfigureAac)?;
        stream.set_time_base(Rational(1, spec.sample_rate as i32));
        stream.set_parameters(&opened);
        stream_index = stream.index();
    }
    output
        .write_header()
        .map_err(AudioFinalizeError::ConfigureAac)?;
    let stream_time_base = output
        .stream(stream_index)
        .expect("new AAC stream disappeared")
        .time_base();
    let frame_size = opened.frame_size().max(1) as usize;
    let mut reader =
        BufReader::new(File::open(mixed_pcm).map_err(|source| io_error(mixed_pcm, source))?);
    let mut interleaved = vec![0_u8; frame_size * usize::from(spec.channels) * 4];
    let mut frame_start = 0_u64;
    while frame_start < spec.total_frames {
        interleaved.fill(0);
        let wanted_frames = (spec.total_frames - frame_start).min(frame_size as u64) as usize;
        let wanted_bytes = wanted_frames * usize::from(spec.channels) * 4;
        reader
            .read_exact(&mut interleaved[..wanted_bytes])
            .map_err(|source| io_error(mixed_pcm, source))?;
        let mut audio_frame = frame::Audio::new(sample_format, frame_size, layout);
        audio_frame.set_rate(spec.sample_rate);
        audio_frame.set_pts(Some(frame_start as i64));
        for channel in 0..usize::from(spec.channels) {
            let plane = audio_frame.plane_mut::<f32>(channel);
            plane.fill(0.0);
            for sample_index in 0..wanted_frames {
                let offset = (sample_index * usize::from(spec.channels) + channel) * 4;
                plane[sample_index] =
                    f32::from_le_bytes(interleaved[offset..offset + 4].try_into().unwrap());
            }
        }
        opened
            .send_frame(&audio_frame)
            .map_err(AudioFinalizeError::SendAudio)?;
        write_audio_packets(
            &mut opened,
            &mut output,
            stream_index,
            Rational(1, spec.sample_rate as i32),
            stream_time_base,
        )?;
        frame_start += wanted_frames as u64;
    }
    opened.send_eof().map_err(AudioFinalizeError::SendAudio)?;
    write_audio_packets(
        &mut opened,
        &mut output,
        stream_index,
        Rational(1, spec.sample_rate as i32),
        stream_time_base,
    )?;
    output
        .write_trailer()
        .map_err(AudioFinalizeError::AudioTrailer)
}

fn write_audio_packets(
    encoder: &mut ffmpeg::encoder::audio::Encoder,
    output: &mut format::context::Output,
    stream_index: usize,
    encoder_time_base: Rational,
    stream_time_base: Rational,
) -> Result<(), AudioFinalizeError> {
    let mut packet = Packet::empty();
    loop {
        match encoder.receive_packet(&mut packet) {
            Ok(()) => {
                packet.set_stream(stream_index);
                packet.set_position(-1);
                packet.rescale_ts(encoder_time_base, stream_time_base);
                packet
                    .write_interleaved(output)
                    .map_err(AudioFinalizeError::AudioPacket)?;
            }
            Err(ffmpeg::Error::Other { errno }) if errno == ffmpeg::error::EAGAIN => break,
            Err(ffmpeg::Error::Eof) => break,
            Err(error) => return Err(AudioFinalizeError::AudioPacket(error)),
        }
    }
    Ok(())
}

pub fn mux_video_and_audio(
    video_path: &Path,
    audio_path: &Path,
    output_path: &Path,
) -> Result<(), AudioFinalizeError> {
    let mut video_input =
        format::input(video_path).map_err(|source| AudioFinalizeError::OpenMuxInput {
            path: video_path.to_owned(),
            source,
        })?;
    let mut audio_input =
        format::input(audio_path).map_err(|source| AudioFinalizeError::OpenMuxInput {
            path: audio_path.to_owned(),
            source,
        })?;
    let video_stream = video_input
        .streams()
        .best(media::Type::Video)
        .ok_or_else(|| AudioFinalizeError::MissingMuxStream {
            path: video_path.to_owned(),
            kind: "video",
        })?;
    let audio_stream = audio_input
        .streams()
        .best(media::Type::Audio)
        .ok_or_else(|| AudioFinalizeError::MissingMuxStream {
            path: audio_path.to_owned(),
            kind: "audio",
        })?;
    let video_index = video_stream.index();
    let audio_index = audio_stream.index();
    let video_time_base = video_stream.time_base();
    let audio_time_base = audio_stream.time_base();
    let video_parameters = video_stream.parameters();
    let audio_parameters = audio_stream.parameters();
    drop(video_stream);
    drop(audio_stream);

    let _ = fs::remove_file(output_path);
    let mut output =
        format::output(output_path).map_err(|source| AudioFinalizeError::CreateMuxOutput {
            path: output_path.to_owned(),
            source,
        })?;
    let output_video_index;
    {
        let mut stream = output
            .add_stream(encoder::find(codec::Id::None))
            .map_err(AudioFinalizeError::ConfigureMux)?;
        stream.set_parameters(video_parameters);
        unsafe { (*stream.parameters().as_mut_ptr()).codec_tag = 0 };
        output_video_index = stream.index();
    }
    let output_audio_index;
    {
        let mut stream = output
            .add_stream(encoder::find(codec::Id::None))
            .map_err(AudioFinalizeError::ConfigureMux)?;
        stream.set_parameters(audio_parameters);
        unsafe { (*stream.parameters().as_mut_ptr()).codec_tag = 0 };
        output_audio_index = stream.index();
    }
    output
        .write_header()
        .map_err(AudioFinalizeError::ConfigureMux)?;
    let output_video_time_base = output
        .stream(output_video_index)
        .expect("mux video stream disappeared")
        .time_base();
    let output_audio_time_base = output
        .stream(output_audio_index)
        .expect("mux audio stream disappeared")
        .time_base();

    let mut video_packet = next_packet(&mut video_input, video_index)?;
    let mut audio_packet = next_packet(&mut audio_input, audio_index)?;
    while video_packet.is_some() || audio_packet.is_some() {
        let take_video = match (&video_packet, &audio_packet) {
            (Some(video), Some(audio)) => {
                packet_seconds(video, video_time_base) <= packet_seconds(audio, audio_time_base)
            }
            (Some(_), None) => true,
            (None, Some(_)) => false,
            (None, None) => break,
        };
        if take_video {
            let mut packet = video_packet.take().unwrap();
            packet.rescale_ts(video_time_base, output_video_time_base);
            packet.set_stream(output_video_index);
            packet.set_position(-1);
            packet
                .write_interleaved(&mut output)
                .map_err(AudioFinalizeError::MuxPacket)?;
            video_packet = next_packet(&mut video_input, video_index)?;
        } else {
            let mut packet = audio_packet.take().unwrap();
            packet.rescale_ts(audio_time_base, output_audio_time_base);
            packet.set_stream(output_audio_index);
            packet.set_position(-1);
            packet
                .write_interleaved(&mut output)
                .map_err(AudioFinalizeError::MuxPacket)?;
            audio_packet = next_packet(&mut audio_input, audio_index)?;
        }
    }
    output
        .write_trailer()
        .map_err(AudioFinalizeError::MuxTrailer)
}

fn next_packet(
    input: &mut format::context::Input,
    stream_index: usize,
) -> Result<Option<Packet>, AudioFinalizeError> {
    loop {
        let mut packet = Packet::empty();
        match packet.read(input) {
            Ok(()) if packet.stream() == stream_index => return Ok(Some(packet)),
            Ok(()) => continue,
            Err(ffmpeg::Error::Eof) => return Ok(None),
            Err(error) => return Err(AudioFinalizeError::MuxPacket(error)),
        }
    }
}

fn packet_seconds(packet: &Packet, time_base: Rational) -> f64 {
    packet.dts().or_else(|| packet.pts()).unwrap_or(0) as f64 * f64::from(time_base)
}

fn seconds_to_frames(seconds: f64, sample_rate: u32) -> Result<u64, AudioFinalizeError> {
    let frames = seconds * f64::from(sample_rate);
    if !frames.is_finite() || frames < 0.0 || frames > u64::MAX as f64 {
        return Err(AudioFinalizeError::TimelineTooLarge);
    }
    Ok(frames.round() as u64)
}

fn nanos_to_frames(nanos: u64, sample_rate: u32) -> u64 {
    ((u128::from(nanos) * u128::from(sample_rate)) / 1_000_000_000_u128).min(u128::from(u64::MAX))
        as u64
}

fn io_error(path: &Path, source: std::io::Error) -> AudioFinalizeError {
    AudioFinalizeError::Io {
        path: path.to_owned(),
        source,
    }
}

fn invalid(path: &Path, detail: impl Into<String>) -> AudioFinalizeError {
    AudioFinalizeError::InvalidSidecar {
        path: path.to_owned(),
        detail: detail.into(),
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::{AudioChunk, write_audio_chunk};
    use std::io::{BufWriter, Write};

    #[test]
    fn overlapping_bus_chunks_are_mixed_before_timeline_encoding() {
        let directory = tempfile::tempdir().unwrap();
        let sidecar = directory.path().join("room.mkv.sfxchunks");
        let mixed = directory.path().join("mixed.f32");
        let mut writer = BufWriter::new(File::create(&sidecar).unwrap());
        writer.write_all(SIDECAR_MAGIC).unwrap();
        for (bus_id, value) in [(1_u16, 0.2_f32), (2_u16, 0.3_f32)] {
            write_audio_chunk(
                &mut writer,
                &AudioChunk {
                    media_time_nanos: 0,
                    sample_rate: 8_000,
                    channels: 2,
                    bus_id,
                    samples: vec![value; 8],
                },
            )
            .unwrap();
        }
        writer.flush().unwrap();

        let spec = render_mix(
            &sidecar,
            &[FinalizeClip {
                source: "room.mkv".to_owned(),
                start_seconds: 1.0 / 8_000.0,
                duration_seconds: 2.0 / 8_000.0,
                music_event: String::new(),
                music_timeline_milliseconds: 0,
            }],
            &mixed,
        )
        .unwrap()
        .unwrap();
        assert_eq!(spec.total_frames, 2);
        let bytes = fs::read(mixed).unwrap();
        let values: Vec<f32> = bytes
            .chunks_exact(4)
            .map(|value| f32::from_le_bytes(value.try_into().unwrap()))
            .collect();
        assert_eq!(values.len(), 4);
        assert!(values.iter().all(|value| (*value - 0.5).abs() < 1e-6));
    }

    #[test]
    fn mapped_bgm_is_decoded_from_its_saved_timeline_position() {
        if std::env::var_os("MQOL_TEST_FFMPEG").is_none() {
            return;
        }
        ffmpeg::init().unwrap();
        let directory = tempfile::tempdir().unwrap();
        let source_pcm = directory.path().join("source.f32");
        let source_audio = directory.path().join("bgm.m4a");
        let sample_rate = 48_000_u32;
        let channels = 2_u16;
        let total_frames = sample_rate as u64;
        let mut pcm = Vec::with_capacity(total_frames as usize * usize::from(channels) * 4);
        for frame in 0..total_frames {
            let value = if frame < total_frames / 2 {
                0.0
            } else {
                (frame as f32 * 440.0 * std::f32::consts::TAU / sample_rate as f32).sin() * 0.25
            };
            for _ in 0..channels {
                pcm.extend_from_slice(&value.to_le_bytes());
            }
        }
        fs::write(&source_pcm, pcm).unwrap();
        encode_aac(
            &source_pcm,
            &source_audio,
            AudioSpec {
                sample_rate,
                channels,
                total_frames,
            },
        )
        .unwrap();

        let event_map = directory.path().join("bgm-map.json");
        fs::write(&event_map, r#"{"event:/test":"bgm.m4a"}"#).unwrap();
        let output_mix = directory.path().join("output.f32");
        let output_audio = directory.path().join("output.m4a");
        let clips = [FinalizeClip {
            source: "room.mkv".to_owned(),
            start_seconds: 0.0,
            duration_seconds: 0.20,
            music_event: "event:/test".to_owned(),
            music_timeline_milliseconds: 600,
        }];
        assert!(
            build_audio_track(
                &directory.path().join("missing.sfxchunks"),
                &clips,
                &output_mix,
                &output_audio,
                Some(&event_map),
            )
            .unwrap()
        );
        let bytes = fs::read(output_mix).unwrap();
        let samples: Vec<f32> = bytes
            .chunks_exact(4)
            .map(|sample| f32::from_le_bytes(sample.try_into().unwrap()))
            .collect();
        let rms = (samples
            .iter()
            .map(|sample| f64::from(*sample) * f64::from(*sample))
            .sum::<f64>()
            / samples.len() as f64)
            .sqrt();
        assert!(rms > 0.05, "unexpected reconstructed BGM RMS {rms}");
        assert!(fs::metadata(output_audio).unwrap().len() > 1_000);
    }
}

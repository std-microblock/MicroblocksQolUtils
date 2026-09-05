use std::{
    mem::size_of,
    path::PathBuf,
    sync::{
        atomic::{AtomicBool, AtomicU8},
        mpsc::{self, sync_channel, SyncSender},
        Arc,
    },
    thread::JoinHandle,
    time::{Duration, SystemTime, UNIX_EPOCH},
};

use pipewire as pw;
use pw::{
    context::Context,
    main_loop::MainLoop,
    properties::properties,
    spa::{
        self,
        param::{
            format::{FormatProperties, MediaSubtype, MediaType},
            video::VideoFormat,
            ParamType,
        },
        pod::{Pod, Property},
        sys::{
            spa_buffer, spa_meta_header, SPA_META_Header, SPA_PARAM_META_size, SPA_PARAM_META_type,
        },
        utils::{Direction, SpaTypes},
    },
    stream::{StreamRef, StreamState},
};

use crate::{
    capturer::Options,
    frame::{BGRxFrame, Frame, RGBFrame, RGBxFrame, VideoFrame, XBGRFrame},
};

use self::{error::LinCapError, portal::ScreenCastPortal};

mod error;
mod portal;

#[derive(Clone)]
struct ListenerUserData {
    pub tx: mpsc::Sender<Frame>,
    pub format: spa::param::video::VideoInfoRaw,
    pub error_flag: Arc<AtomicBool>,
}

fn param_changed_callback(
    _stream: &StreamRef,
    user_data: &mut ListenerUserData,
    id: u32,
    param: Option<&Pod>,
) {
    let Some(param) = param else {
        return;
    };
    if id != pw::spa::param::ParamType::Format.as_raw() {
        return;
    }
    let (media_type, media_subtype) = match pw::spa::param::format_utils::parse_format(param) {
        Ok(v) => v,
        Err(_) => return,
    };

    if media_type != MediaType::Video || media_subtype != MediaSubtype::Raw {
        return;
    }

    user_data
        .format
        .parse(param)
        // TODO: Tell library user of the error
        .expect("Failed to parse format parameter");
}

fn state_changed_callback(
    _stream: &StreamRef,
    user_data: &mut ListenerUserData,
    _old: StreamState,
    new: StreamState,
) {
    match new {
        StreamState::Error(e) => {
            eprintln!("pipewire: State changed to error({e})");
            user_data
                .error_flag
                .store(true, std::sync::atomic::Ordering::Relaxed);
        }
        _ => {}
    }
}

unsafe fn get_timestamp(buffer: *mut spa_buffer) -> i64 {
    let n_metas = (*buffer).n_metas;
    if n_metas > 0 {
        let mut meta_ptr = (*buffer).metas;
        let metas_end = (*buffer).metas.wrapping_add(n_metas as usize);
        while meta_ptr != metas_end {
            if (*meta_ptr).type_ == SPA_META_Header {
                let meta_header: &mut spa_meta_header =
                    &mut *((*meta_ptr).data as *mut spa_meta_header);
                return meta_header.pts;
            }
            meta_ptr = meta_ptr.wrapping_add(1);
        }
        0
    } else {
        0
    }
}

fn process_callback(stream: &StreamRef, user_data: &mut ListenerUserData) {
    let buffer = unsafe { stream.dequeue_raw_buffer() };
    if !buffer.is_null() {
        'outside: {
            let buffer = unsafe { (*buffer).buffer };
            if buffer.is_null() {
                break 'outside;
            }
            let timestamp = unsafe { get_timestamp(buffer) };

            let n_datas = unsafe { (*buffer).n_datas };
            if n_datas < 1 {
                break 'outside;
            }
            let frame_size = user_data.format.size();
            let format = user_data.format.format();
            let bytes_per_pixel = if format == VideoFormat::RGB { 3 } else { 4 };
            let row_bytes = frame_size.width as usize * bytes_per_pixel;
            let data = unsafe { &*(*buffer).datas };
            if data.data.is_null() || data.chunk.is_null() {
                break 'outside;
            }
            let chunk = unsafe { &*data.chunk };
            let negative_stride = chunk.stride < 0;
            let stride = chunk.stride.unsigned_abs() as usize;
            let stride = if stride == 0 { row_bytes } else { stride };
            let required = stride
                .saturating_mul(frame_size.height.saturating_sub(1) as usize)
                .saturating_add(row_bytes);
            let Some(data_end) = (chunk.offset as usize).checked_add(required) else {
                break 'outside;
            };
            if required > chunk.size as usize
                || data_end > data.maxsize as usize
                || row_bytes > stride
            {
                break 'outside;
            }
            let source = unsafe { (data.data as *const u8).add(chunk.offset as usize) };
            let mut frame_data = Vec::with_capacity(row_bytes * frame_size.height as usize);
            for row in 0..frame_size.height as usize {
                let source_row = if negative_stride {
                    frame_size.height as usize - row - 1
                } else {
                    row
                };
                let bytes = unsafe {
                    std::slice::from_raw_parts(source.add(source_row * stride), row_bytes)
                };
                frame_data.extend_from_slice(bytes);
            }
            let display_time = if timestamp >= 0 {
                UNIX_EPOCH + Duration::from_nanos(timestamp as u64)
            } else {
                SystemTime::now()
            };

            if let Err(e) = match format {
                VideoFormat::RGBx => user_data.tx.send(Frame::Video(VideoFrame::RGBx(RGBxFrame {
                    display_time,
                    width: frame_size.width as i32,
                    height: frame_size.height as i32,
                    data: frame_data,
                }))),
                VideoFormat::RGB => user_data.tx.send(Frame::Video(VideoFrame::RGB(RGBFrame {
                    display_time,
                    width: frame_size.width as i32,
                    height: frame_size.height as i32,
                    data: frame_data,
                }))),
                VideoFormat::xBGR => user_data.tx.send(Frame::Video(VideoFrame::XBGR(XBGRFrame {
                    display_time,
                    width: frame_size.width as i32,
                    height: frame_size.height as i32,
                    data: frame_data,
                }))),
                VideoFormat::BGRx => user_data.tx.send(Frame::Video(VideoFrame::BGRx(BGRxFrame {
                    display_time,
                    width: frame_size.width as i32,
                    height: frame_size.height as i32,
                    data: frame_data,
                }))),
                _ => panic!("Unsupported frame format received"),
            } {
                eprintln!("{e}");
            }
        }
    } else {
        eprintln!("Out of buffers");
    }

    unsafe { stream.queue_raw_buffer(buffer) };
}

// TODO: Format negotiation
fn pipewire_capturer(
    options: Options,
    tx: mpsc::Sender<Frame>,
    ready_sender: &SyncSender<bool>,
    stream_id: u32,
    state: Arc<AtomicU8>,
    error_flag: Arc<AtomicBool>,
) -> Result<(), LinCapError> {
    pw::init();

    let mainloop = MainLoop::new(None)?;
    let context = Context::new(&mainloop)?;
    let core = context.connect(None)?;

    let user_data = ListenerUserData {
        tx,
        format: Default::default(),
        error_flag: error_flag.clone(),
    };

    let stream = pw::stream::Stream::new(
        &core,
        "scap",
        properties! {
            *pw::keys::MEDIA_TYPE => "Video",
            *pw::keys::MEDIA_CATEGORY => "Capture",
            *pw::keys::MEDIA_ROLE => "Screen",
        },
    )?;

    let _listener = stream
        .add_local_listener_with_user_data(user_data.clone())
        .state_changed(state_changed_callback)
        .param_changed(param_changed_callback)
        .process(process_callback)
        .register()?;

    let obj = pw::spa::pod::object!(
        pw::spa::utils::SpaTypes::ObjectParamFormat,
        pw::spa::param::ParamType::EnumFormat,
        pw::spa::pod::property!(FormatProperties::MediaType, Id, MediaType::Video),
        pw::spa::pod::property!(FormatProperties::MediaSubtype, Id, MediaSubtype::Raw),
        pw::spa::pod::property!(
            FormatProperties::VideoFormat,
            Choice,
            Enum,
            Id,
            pw::spa::param::video::VideoFormat::RGB,
            pw::spa::param::video::VideoFormat::RGBx,
            pw::spa::param::video::VideoFormat::xBGR,
            pw::spa::param::video::VideoFormat::BGRx,
        ),
        pw::spa::pod::property!(
            FormatProperties::VideoSize,
            Choice,
            Range,
            Rectangle,
            pw::spa::utils::Rectangle {
                // Default
                width: 128,
                height: 128,
            },
            pw::spa::utils::Rectangle {
                // Min
                width: 1,
                height: 1,
            },
            pw::spa::utils::Rectangle {
                // Max
                width: 4096,
                height: 4096,
            }
        ),
        pw::spa::pod::property!(
            FormatProperties::VideoMaxFramerate,
            Fraction,
            pw::spa::utils::Fraction {
                num: options.fps,
                denom: 1
            }
        ),
    );

    let metas_obj = pw::spa::pod::object!(
        SpaTypes::ObjectParamMeta,
        ParamType::Meta,
        Property::new(
            SPA_PARAM_META_type,
            pw::spa::pod::Value::Id(pw::spa::utils::Id(SPA_META_Header))
        ),
        Property::new(
            SPA_PARAM_META_size,
            pw::spa::pod::Value::Int(size_of::<pw::spa::sys::spa_meta_header>() as i32)
        ),
    );

    let values: Vec<u8> = pw::spa::pod::serialize::PodSerializer::serialize(
        std::io::Cursor::new(Vec::new()),
        &pw::spa::pod::Value::Object(obj),
    )?
    .0
    .into_inner();
    let metas_values: Vec<u8> = pw::spa::pod::serialize::PodSerializer::serialize(
        std::io::Cursor::new(Vec::new()),
        &pw::spa::pod::Value::Object(metas_obj),
    )?
    .0
    .into_inner();

    let mut params = [
        pw::spa::pod::Pod::from_bytes(&values).unwrap(),
        pw::spa::pod::Pod::from_bytes(&metas_values).unwrap(),
    ];

    stream.connect(
        Direction::Input,
        Some(stream_id),
        pw::stream::StreamFlags::AUTOCONNECT | pw::stream::StreamFlags::MAP_BUFFERS,
        &mut params,
    )?;

    ready_sender.send(true)?;

    while state.load(std::sync::atomic::Ordering::Relaxed) == 0 {
        std::thread::sleep(Duration::from_millis(10));
    }

    let pw_loop = mainloop.loop_();

    // User has called Capturer::start() and we start the main loop
    while state.load(std::sync::atomic::Ordering::Relaxed) == 1
        && /* If the stream state got changed to `Error`, we exit. TODO: tell user that we exited */
          !error_flag.load(std::sync::atomic::Ordering::Relaxed)
    {
        pw_loop.iterate(Duration::from_millis(100));
    }

    Ok(())
}

pub struct LinuxCapturer {
    capturer_join_handle: Option<JoinHandle<Result<(), LinCapError>>>,
    // The pipewire stream is deleted when the connection is dropped.
    // That's why we keep it alive
    _connection: dbus::blocking::Connection,
    state: Arc<AtomicU8>,
    error_flag: Arc<AtomicBool>,
}

impl LinuxCapturer {
    // TODO: Error handling
    pub fn new(options: &Options, tx: mpsc::Sender<Frame>) -> Self {
        let connection =
            dbus::blocking::Connection::new_session().expect("Failed to create dbus connection");
        let stream_id = ScreenCastPortal::new(&connection)
            .show_cursor(options.show_cursor)
            .expect("Unsupported cursor mode")
            .restore_token_path(options.restore_token_path.clone())
            .create_stream()
            .expect("Failed to get screencast stream")
            .pw_node_id();

        // TODO: Fix this hack
        let options = options.clone();
        let (ready_sender, ready_recv) = sync_channel(1);
        let state = Arc::new(AtomicU8::new(0));
        let error_flag = Arc::new(AtomicBool::new(false));
        let thread_state = state.clone();
        let thread_error_flag = error_flag.clone();
        let capturer_join_handle = std::thread::spawn(move || {
            let res = pipewire_capturer(
                options,
                tx,
                &ready_sender,
                stream_id,
                thread_state,
                thread_error_flag,
            );
            if res.is_err() {
                ready_sender.send(false)?;
            }
            res
        });

        if !ready_recv.recv().expect("Failed to receive") {
            panic!("Failed to setup capturer");
        }

        Self {
            capturer_join_handle: Some(capturer_join_handle),
            _connection: connection,
            state,
            error_flag,
        }
    }

    pub fn start_capture(&self) {
        self.state.store(1, std::sync::atomic::Ordering::Relaxed);
    }

    pub fn stop_capture(&mut self) {
        self.state.store(2, std::sync::atomic::Ordering::Relaxed);
        if let Some(handle) = self.capturer_join_handle.take() {
            if let Err(e) = handle.join().expect("Failed to join capturer thread") {
                eprintln!("Error occured capturing: {e}");
            }
        }
        self.state.store(0, std::sync::atomic::Ordering::Relaxed);
        self.error_flag
            .store(false, std::sync::atomic::Ordering::Relaxed);
    }
}

pub fn create_capturer(options: &Options, tx: mpsc::Sender<Frame>) -> LinuxCapturer {
    LinuxCapturer::new(options, tx)
}

/// Run the desktop portal source-selection flow and persist the restore token
/// without starting a capture stream. This allows an application to (re-)authorize
/// screen capture ahead of time, instead of blocking the first capture session.
pub fn authorize_source(
    restore_token_path: Option<PathBuf>,
    force: bool,
) -> Result<(), LinCapError> {
    let connection = dbus::blocking::Connection::new_session()
        .map_err(|error| LinCapError::new(format!("dbus session: {error}")))?;
    ScreenCastPortal::new(&connection)
        .restore_token_path(restore_token_path)
        .authorize(force)
}

/// Whether a persisted source selection can be restored, so the picker can be skipped.
pub fn has_authorization(restore_token_path: Option<PathBuf>) -> bool {
    ScreenCastPortal::has_restore_token(restore_token_path)
}

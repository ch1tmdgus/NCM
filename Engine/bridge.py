"""Private newline-delimited JSON IPC for the native WinUI 3 front end."""
import base64
import ctypes
import io
import json
import sys
import threading
import time

import keyboard
import pyautogui
from PIL import ImageGrab
from macro_core import MacroCore


_output_lock = threading.Lock()

def emit(kind, **data):
    with _output_lock:
        print(json.dumps(dict(type=kind, **data), ensure_ascii=False), flush=True)

class Value:
    def __init__(self, value): self.value = value
    def get(self): return self.value
    def set(self, value): self.value = value

class Root:
    def after(self, delay, callback): callback()
    def winfo_screenwidth(self): return ctypes.windll.user32.GetSystemMetrics(0)
    def winfo_screenheight(self): return ctypes.windll.user32.GetSystemMetrics(1)

class Counter:
    def config(self, **values): emit('count', text=values['text'])

class Engine(MacroCore):
    def __init__(self):
        self.root = Root()
        self.count_label = Counter()
        self.cancel = threading.Event()
        self.gate = threading.Lock()
        self.busy = False
        self.success_pending = False
        self.all_setup_notified = False
        self.suppress_escape_until = 0.0
        self.monitor_region = None
        self.target_colors = []
        self.refresh_click_pos_1 = self.refresh_click_pos_2 = None
        self.focus_click_pos = self.final_click_pos = None
        self.is_running = False
        self.refresh_count = 0
        self.SCAN_STEP, self.CHECK_DIST, self.AUTO_RETRY, self.RETRY_MAX = 3, 1, True, 3
        self.clr_primary, self.clr_success, self.clr_danger = '#4e73df', '#1cc88a', '#e74a3b'
        for name, default in dict(refresh_delay=0.2, is_random_delay=False,
                use_custom_refresh=False, use_multi_refresh=False, no_refresh_mode=False,
                is_yes24_mode=False, start_wait_time=3, scan_method=1).items():
            setattr(self, name, Value(default))

    def add_log(self, msg): emit('log', text=time.strftime('[%H:%M:%S] ')+str(msg))
    def update_status(self, text, color): emit('status', text=text, color=color)
    def state(self):
        complete = bool(self.monitor_region and self.target_colors and self.final_click_pos)
        complete = complete and (not self.is_yes24_mode.get() or bool(self.focus_click_pos))
        complete = complete and (not (self.use_custom_refresh.get() or self.use_multi_refresh.get()) or bool(self.refresh_click_pos_1))
        complete = complete and (not self.use_multi_refresh.get() or bool(self.refresh_click_pos_2))
        if complete and not self.all_setup_notified:
            self.add_log('모든 필수 설정 완료! 매크로를 시작할 수 있습니다.')
            if not self.busy: self.update_status('모든 설정 완료', self.clr_success)
        self.all_setup_notified = complete
        emit('state', busy=self.busy, region=self.monitor_region, colors=self.target_colors,
             focus=self.focus_click_pos, final=self.final_click_pos,
             refresh1=self.refresh_click_pos_1, refresh2=self.refresh_click_pos_2)

    def apply_settings(self, settings):
        if self.busy: return
        specs = dict(refresh_delay=(float, 0.1, 3.0), start_wait_time=(int, 0, 3), scan_method=(int, 1, 2))
        for key, value in settings.items():
            if key in specs:
                convert, low, high = specs[key]
                getattr(self, key).set(max(low, min(high, convert(value))))
            elif key in ('is_random_delay','use_custom_refresh','use_multi_refresh','no_refresh_mode','is_yes24_mode'):
                if not isinstance(value, bool): raise ValueError('잘못된 토글 값')
                getattr(self, key).set(value)
        if self.use_multi_refresh.get(): self.use_custom_refresh.set(False)
        if not self.is_yes24_mode.get(): self.focus_click_pos = None
        self.state()


    def launch(self, task):
        if not self.gate.acquire(blocking=False): return
        self.busy = True
        self.success_pending = False
        self.cancel.clear()
        self.state()
        def work():
            try: task()
            except InterruptedError: self.update_status('중지됨', self.clr_danger)
            except Exception as ex:
                self.add_log('작업 오류: '+str(ex))
                self.update_status('오류 발생', self.clr_danger)
                emit('warning', title='작업 오류', text=str(ex))
            finally:
                self.is_running = False
                self.busy = False
                self.gate.release()
                self.state()
        threading.Thread(target=work, daemon=True).start()

    def wait_key(self, key):
        while keyboard.is_pressed(key):
            if self.cancel.wait(0.02): raise InterruptedError()
        while not keyboard.is_pressed(key):
            if self.cancel.wait(0.01): raise InterruptedError()
        if self.cancel.is_set(): raise InterruptedError()
        return tuple(pyautogui.position())

    def select(self, kind):
        def work():
            if kind == 'region':
                self.update_status('시작점에 마우스를 놓고 A', self.clr_primary)
                x1, y1 = self.wait_key('a')
                self.update_status('끝점에 마우스를 놓고 B', self.clr_primary)
                x2, y2 = self.wait_key('b')
                region = (min(x1,x2), min(y1,y2), max(x1,x2), max(y1,y2))
                if region[0] == region[2] or region[1] == region[3]: raise ValueError('영역의 너비와 높이는 1픽셀 이상이어야 합니다.')
                image = ImageGrab.grab(bbox=region, all_screens=True)
                image.thumbnail((640,400))
                buffer = io.BytesIO()
                image.save(buffer, format='PNG')
                self.monitor_region = region
                emit('preview', png=base64.b64encode(buffer.getvalue()).decode())
            else:
                self.update_status('마우스로 위치를 가리킨 뒤 A', self.clr_primary)
                if kind == 'color': emit('picker', active=True)
                pos = self.wait_key('a')
                if kind == 'color':
                    emit('picker', active=False)
                    if self.cancel.wait(0.08): raise InterruptedError()
                    # A one-pixel capture handles negative multi-monitor coordinates.
                    color = tuple(ImageGrab.grab(bbox=(pos[0],pos[1],pos[0]+1,pos[1]+1), all_screens=True).convert('RGB').getpixel((0,0)))
                    if color not in self.target_colors: self.target_colors.append(color)
                else:
                    attr = dict(focus='focus_click_pos', final='final_click_pos',
                                refresh1='refresh_click_pos_1', refresh2='refresh_click_pos_2')[kind]
                    setattr(self, attr, pos)
            self.add_log('설정 완료: '+kind)
            self.update_status('설정 완료', self.clr_success)
        self.launch(work)

    def start(self, immediate=False):
        if self.busy: return
        if not (self.monitor_region and self.target_colors and self.final_click_pos):
            emit('warning', title='필수 설정', text='감지 영역, 좌석 색상, 최종 클릭 위치를 지정하세요.'); self.state(); return
        if self.is_yes24_mode.get() and not self.focus_click_pos:
            emit('warning', title='YES24 설정', text='회차창 위치를 지정하세요.'); return
        if (self.use_multi_refresh.get() and not (self.refresh_click_pos_1 and self.refresh_click_pos_2)) or (self.use_custom_refresh.get() and not self.refresh_click_pos_1):
            emit('warning', title='새로고침 설정', text='사용할 새로고침 위치를 모두 지정하세요.'); return
        def work():
            wait = 0 if immediate else self.start_wait_time.get()
            for remaining in range(wait, 0, -1):
                self.update_status(f'{remaining}초 후 시작', '#fd7e14')
                if self.cancel.wait(1): raise InterruptedError()
            if self.cancel.is_set(): raise InterruptedError()
            self.is_running, self.refresh_count = True, 0
            emit('count', text='작업 횟수: 0회')
            if self.is_yes24_mode.get():
                self._click_at(*self.focus_click_pos)
                if self.cancel.wait(0.1): raise InterruptedError()
                self.suppress_escape_until = time.monotonic() + 0.3
                pyautogui.press('esc')
                if self.cancel.wait(0.1): raise InterruptedError()
            self.macro_loop()
        self.launch(work)

    def show_success_popup(self):
        if self.cancel.is_set(): return
        self.success_pending = True
        self.update_status('좌석 감지 및 클릭 완료', self.clr_success)
        emit('success', text='좌석 감지 및 클릭을 완료했습니다. 예매 화면에서 결과를 확인하세요.\nA: 다시 시작 / ESC: 종료')

    def restart(self):
        if self.success_pending and not self.busy:
            self.success_pending = False
            emit('dismiss_success')
            self.start(immediate=True)

    def stop_macro(self):
        self.cancel.set()
        self.is_running = False
        self.success_pending = False
        emit('dismiss_success')
        self.update_status('중지됨', self.clr_danger)

    def command(self, message):
        action = message.get('action')
        if action == 'settings': self.apply_settings(message.get('settings', {}))
        elif action == 'select': self.select(message['kind'])
        elif action == 'remove_color' and not self.busy:
            color = tuple(message['color'])
            if color in self.target_colors: self.target_colors.remove(color)
            self.state()
        elif action == 'start': self.start()
        elif action == 'restart': self.restart()
        elif action == 'stop': self.stop_macro()
        elif action == 'state': self.state()


def main():
    if sys.platform != 'win32': raise RuntimeError('이 엔진은 Windows 전용입니다.')
    for stream in (sys.stdin, sys.stdout, sys.stderr):
        if hasattr(stream, 'reconfigure'): stream.reconfigure(encoding='utf-8')
    try: ctypes.windll.shcore.SetProcessDpiAwareness(1)
    except Exception: pass
    engine = Engine()
    # A polling watcher avoids treating the engine's own YES24 Escape as user input.
    def watch():
        previous_escape = previous_a = False
        while True:
            esc, a = keyboard.is_pressed('esc'), keyboard.is_pressed('a')
            if esc and not previous_escape and time.monotonic() >= engine.suppress_escape_until: engine.stop_macro()
            if a and not previous_a: engine.restart()
            previous_escape, previous_a = esc, a
            time.sleep(0.02)
    threading.Thread(target=watch, daemon=True).start()
    engine.add_log('NCM X WinUI 3 준비 완료')
    emit('ready')
    engine.state()
    try:
        for line in sys.stdin:
            try: engine.command(json.loads(line))
            except Exception as ex: engine.add_log('요청 처리 오류: '+str(ex))
    finally:
        engine.stop_macro()
        keyboard.unhook_all()

if __name__ == '__main__':
    if '--self-test' in sys.argv:
        import numpy as np
        import numpy.version
        from PIL import Image
        assert int(np.arange(6).reshape(2, 3).sum()) == 15
        assert Image.fromarray(np.zeros((2, 2, 3), dtype=np.uint8)).size == (2, 2)
        print(json.dumps(dict(type='self_test', ok=True, numpy=np.__version__)), flush=True)
    else:
        main()

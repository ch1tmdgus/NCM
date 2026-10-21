# Detection and click algorithms retained from macro_11_fixed.py.
import time, random, platform, winsound
import pyautogui, keyboard, mss
import numpy as np
from PIL import ImageGrab

class MacroCore:
    def puzzle_alert_logic(self):
        self.add_log("⚠️ 퍼즐 감지! 마우스를 움직여 해제하세요.")
        orig_pos = pyautogui.position()
        while self.is_running:
            curr_pos = pyautogui.position()
            if abs(curr_pos.x - orig_pos.x) > 5 or abs(curr_pos.y - orig_pos.y) > 5:
                self.add_log("✅ 개입 확인. 경고 종료.")
                break
            winsound.Beep(1200, 200); winsound.Beep(800, 200); time.sleep(0.1)

    def _pick_seat_point(self, match_indices):
        """첫 감지 픽셀과 이어진 좌석 덩어리의 중심을 구하되,
        반드시 '실제로 색이 일치한 픽셀' 위의 좌표(서브샘플 좌표)를 반환한다.
        (기존: 주변 픽셀 평균 -> 좌석 사이 빈틈/다른 좌석에 찍혀 클릭이 헛도는 경우 발생)"""
        first_y, first_x = int(match_indices[0][0]), int(match_indices[0][1])
        win = match_indices[(match_indices[:, 0] <= first_y + 15) &
                            (match_indices[:, 1] >= first_x - 15) &
                            (match_indices[:, 1] <= first_x + 15)]
        pts = set(map(tuple, win.tolist()))
        start = (first_y, first_x)
        seen = {start}
        stack = [start]
        while stack:
            py_, px_ = stack.pop()
            for dy in (-1, 0, 1):
                for dx in (-1, 0, 1):
                    nb = (py_ + dy, px_ + dx)
                    if nb in pts and nb not in seen:
                        seen.add(nb)
                        stack.append(nb)
        arr = np.array(list(seen))
        mean_y, mean_x = arr.mean(axis=0)
        inner = [p for p in seen if all(q in seen for q in
                 ((p[0] - 1, p[1]), (p[0] + 1, p[1]), (p[0], p[1] - 1), (p[0], p[1] + 1)))]
        cand = np.array(inner) if inner else arr
        k = int(np.argmin((cand[:, 0] - mean_y) ** 2 + (cand[:, 1] - mean_x) ** 2))
        return int(cand[k, 0]), int(cand[k, 1])

    def _refine_click_point(self, sct, t_color, ax, ay, delta, bounds, R=40):
        """감지 직후 좌석 주변만 다시 캡처해 풀해상도로 정중앙을 재계산한다.
        (3픽셀 간격 샘플링, 화면 갱신 도중(티어링)으로 좌석 가장자리만 잡힌 경우에도 정중앙을 클릭)
        실패/좌석 소실 시에는 입력 좌표를 그대로 반환한다. 반환: (x, y, 좌석크기 또는 None)"""
        try:
            b_left, b_top, b_right, b_bottom = bounds
            left, top = max(b_left, ax - R), max(b_top, ay - R)
            right, bottom = min(b_right, ax + R + 1), min(b_bottom, ay + R + 1)
            if right - left < 3 or bottom - top < 3:
                return ax, ay, None
            shot = np.array(sct.grab({"left": int(left), "top": int(top),
                                      "width": int(right - left), "height": int(bottom - top)}))[:, :, :3]
            mask = np.all(np.abs(shot.astype(np.int16) - t_color.astype(np.int16)) < delta, axis=-1)
            sy, sx = int(ay - top), int(ax - left)
            if not (0 <= sy < mask.shape[0] and 0 <= sx < mask.shape[1]):
                return ax, ay, None
            ly, lx = sy, sx
            if not mask[ly, lx]:
                pts = np.argwhere(mask)
                if pts.size == 0:
                    return ax, ay, None
                k = int(np.argmin((pts[:, 0] - sy) ** 2 + (pts[:, 1] - sx) ** 2))
                ly, lx = int(pts[k, 0]), int(pts[k, 1])
                if (ly - sy) ** 2 + (lx - sx) ** 2 > 12 ** 2:
                    return ax, ay, None

            def run(line, i):
                lo = hi = i
                while lo > 0 and line[lo - 1]: lo -= 1
                while hi < len(line) - 1 and line[hi + 1]: hi += 1
                return lo, hi

            for _ in range(2):
                l, r = run(mask[ly, :], lx); lx = (l + r) // 2
                t, b = run(mask[:, lx], ly); ly = (t + b) // 2
            if not mask[ly, lx]:
                return ax, ay, None
            return int(left + lx), int(top + ly), (r - l + 1, b - t + 1)
        except Exception:
            return ax, ay, None

    def _seat_available(self, sct, t_color, x, y, delta, bounds, R=5):
        """클릭 지점 주변에 '선택 가능 좌석 색'이 아직 남아있는지 확인.
        판단이 불가능한 경우(오류/영역 밖)는 False -> 재시도하지 않음(안전 쪽)."""
        try:
            b_left, b_top, b_right, b_bottom = bounds
            left, top = max(b_left, x - R), max(b_top, y - R)
            right, bottom = min(b_right, x + R + 1), min(b_bottom, y + R + 1)
            if right - left < 1 or bottom - top < 1:
                return False
            shot = np.array(sct.grab({"left": int(left), "top": int(top),
                                      "width": int(right - left), "height": int(bottom - top)}))[:, :, :3]
            mask = np.all(np.abs(shot.astype(np.int16) - t_color.astype(np.int16)) < delta, axis=-1)
            return int(mask.sum()) >= 4
        except Exception:
            return False

    def _verify_and_retry(self, sct, t_color, x, y, delta, bounds):
        """최초 클릭(속도)에는 영향 없이, 클릭이 끝난 뒤에 좌석 반영 여부를 확인한다.
        좌석이 그대로 남아있으면 클릭이 반영되지 않은 것이므로
        재조준 -> 커서 올리기(30ms) -> 단일 클릭 -> 최종 클릭 순으로 재시도한다."""
        try:
            for attempt in range(self.RETRY_MAX + 1):
                if self.cancel.is_set(): return False
                end = time.time() + 0.12
                reflected = False
                while True:
                    time.sleep(0.02)
                    if not self._seat_available(sct, t_color, x, y, delta, bounds):
                        reflected = True
                        break
                    if time.time() >= end:
                        break
                if reflected:
                    if attempt:
                        self.add_log(f"✅ 재시도 {attempt}회 만에 좌석 반영 확인")
                    return True
                if attempt >= self.RETRY_MAX:
                    break
                x, y, _ = self._refine_click_point(sct, t_color, x, y, delta, bounds)
                self.add_log(f"🔁 클릭 미반영 감지 -> 재시도 {attempt + 1}/{self.RETRY_MAX} ({x}, {y})")
                self._click_at(x, y, clicks=1, hover=0.03)
                self._click_at(*self.final_click_pos)
            self.add_log("⚠️ 재시도 후에도 좌석이 남아 있습니다. 화면을 직접 확인하세요.")
        except Exception as e:
            self.add_log(f"⚠️ 반영 확인 중 오류: {e}")
        return False

    def _click_at(self, x, y, clicks=1, interval=0.0, hover=0.0):
        """커서가 목표 좌표에 실제 도착했는지 확인 후 클릭. 예외가 나도 클릭은 반드시 시도.
        (정상 동작 시 추가되는 시간은 마이크로초 단위)"""
        if self.cancel.is_set():
            raise InterruptedError("중지 요청")
        x, y = int(x), int(y)
        prev_failsafe = pyautogui.FAILSAFE
        pyautogui.FAILSAFE = False  # 커서가 화면 모서리에 있어도 클릭이 중단되지 않게
        try:
            for _ in range(3):
                pyautogui.moveTo(x, y, _pause=False)
                p = pyautogui.position()
                if abs(p.x - x) <= 1 and abs(p.y - y) <= 1:
                    break
            if hover:
                time.sleep(hover)  # 재시도 때만 사용: 커서가 좌석 위에 올라간 뒤 클릭
            pyautogui.click(x, y, clicks=clicks, interval=interval, _pause=False)
        except Exception as e:
            self.add_log(f"⚠️ 클릭 오류({e}) - 재시도")
            try:
                pyautogui.click(x, y, clicks=clicks, interval=interval, _pause=False)
            except Exception as e2:
                self.add_log(f"❌ 클릭 재시도 실패: {e2}")
        finally:
            pyautogui.FAILSAFE = prev_failsafe

    def macro_loop(self):
        pyautogui.PAUSE = 0.0  
        delta = 25
        step = self.SCAN_STEP
        dist = self.CHECK_DIST
        self.update_status("매크로 실행 중", self.clr_success)
        
        puzzle_color = np.array([241, 137, 133])
        compiled_colors = [np.array([tc[2], tc[1], tc[0]]) for tc in self.target_colors]
        
        mss_region = {
            "top": int(self.monitor_region[1]),
            "left": int(self.monitor_region[0]),
            "width": int(self.monitor_region[2] - self.monitor_region[0]),
            "height": int(self.monitor_region[3] - self.monitor_region[1])
        }

        screen_w = self.root.winfo_screenwidth()
        screen_h = self.root.winfo_screenheight()
        mss_full = {"top": 0, "left": 0, "width": screen_w, "height": screen_h}
        mss_bounds = (mss_region["left"], mss_region["top"],
                      mss_region["left"] + mss_region["width"], mss_region["top"] + mss_region["height"])

        with mss.mss() as sct:
            while self.is_running:
                if keyboard.is_pressed('esc'): self.stop_macro(); break
                
                loop_start = time.time()
                
                # 1. 퍼즐 화면 체크 (신형/구형 공통 적용)
                if not self.no_refresh_mode.get():
                    try:
                        full_img = np.array(sct.grab(mss_full))[::100, ::100, :3]
                        if np.any(np.all(np.abs(full_img - puzzle_color) < 15, axis=-1)):
                            self.puzzle_alert_logic()
                            time.sleep(1.0)
                            continue
                    except: pass

                found = False
                react_ms = None

                # 2. 좌석 감지 로직 분기
                if self.scan_method.get() == 1:
                    # [신형] numpy 고속 스캔 및 픽셀 그룹화 정중앙 클릭
                    try: img_sct = sct.grab(mss_region)
                    except: continue
                    
                    img_np = np.array(img_sct)
                    if img_np.size == 0: continue
                    img_bgr = img_np[:, :, :3]
                    sub_img = img_bgr[::step, ::step, :]
                    
                    for t_color in compiled_colors:
                        color_mask = np.all(np.abs(sub_img.astype(np.int16) - t_color.astype(np.int16)) < delta, axis=-1)
                        match_indices = np.argwhere(color_mask)
                        
                        if match_indices.size > 0:
                            center_y, center_x = self._pick_seat_point(match_indices)
                            
                            actual_x = mss_region["left"] + (center_x * step)
                            actual_y = mss_region["top"] + (center_y * step)
                            
                            if self.cancel.is_set(): return
                            self.is_running, found = False, True
                            rx, ry, sz = self._refine_click_point(sct, t_color, actual_x, actual_y, delta, mss_bounds)
                            self._click_at(rx, ry, clicks=2, interval=0.001)
                            cur = pyautogui.position()
                            self._click_at(*self.final_click_pos)
                            react_ms = (time.time() - loop_start) * 1000
                            self.add_log(f"🖱️ 좌석 클릭 좌표: ({rx}, {ry}) / 감지점 ({actual_x}, {actual_y}) / 좌석 크기 {sz} / 클릭 직후 커서 ({cur.x}, {cur.y})")
                            if self.AUTO_RETRY:
                                self._verify_and_retry(sct, t_color, rx, ry, delta, mss_bounds)
                            break
                            
                else:
                    # [구형] PIL ImageGrab 스캔 및 십자 픽셀 검증 후 모서리 클릭
                    img_pil = ImageGrab.grab(bbox=self.monitor_region)
                    w, h = img_pil.size[0], img_pil.size[1]
                    for x in range(dist, w - dist, step):
                        if self.cancel.is_set(): return
                        for y in range(dist, h - dist, step):
                            px = img_pil.getpixel((x, y))
                            for t_color in self.target_colors:
                                if all(abs(px[i] - t_color[i]) < delta for i in range(3)):
                                    pts = [
                                        img_pil.getpixel((x-dist, y-dist)), 
                                        img_pil.getpixel((x+dist, y-dist)), 
                                        img_pil.getpixel((x-dist, y+dist)), 
                                        img_pil.getpixel((x+dist, y+dist))
                                    ]
                                    if all(all(abs(p[i]-t_color[i]) < delta for i in range(3)) for p in pts):
                                        self.is_running, found = False, True
                                        self._click_at(self.monitor_region[0] + x, self.monitor_region[1] + y)
                                        time.sleep(0.01)
                                        self._click_at(*self.final_click_pos)
                                        break
                            if found: break
                        if found: break
                
                # 3. 선점 완료 처리
                if self.cancel.is_set(): return
                if found:
                    fps_time = react_ms if react_ms is not None else (time.time() - loop_start) * 1000
                    self.add_log(f"🎯 선점 완벽 성공! 반응 타임: {fps_time:.1f}ms")
                    self.play_success_sound()
                    self.show_success_popup()
                    break
                
                # 4. 새로고침 로직
                if not self.no_refresh_mode.get():
                    if self.is_yes24_mode.get():
                        pyautogui.press('up'); pyautogui.press('down')
                    elif self.use_multi_refresh.get() and self.refresh_click_pos_1 and self.refresh_click_pos_2:
                        target_ref = random.choice([self.refresh_click_pos_1, self.refresh_click_pos_2])
                        pyautogui.click(target_ref)
                    elif self.use_custom_refresh.get() and self.refresh_click_pos_1:
                        pyautogui.click(self.refresh_click_pos_1)
                    else:
                        pyautogui.press('f5')
                
                self.refresh_count += 1
                self.root.after(0, lambda: self.count_label.config(text=f"작업 횟수: {self.refresh_count}회"))
                delay = random.uniform(0.2, 0.5) if self.is_random_delay.get() else self.refresh_delay.get()
                self.cancel.wait(delay)

    def play_success_sound(self):
        if platform.system() == "Windows": winsound.Beep(1000, 200); winsound.Beep(1200, 200)

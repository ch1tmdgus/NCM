"""Headless regression checks. No real mouse/keyboard/network actions are performed."""
import importlib
import socket
from pathlib import Path
import sys
import threading
import time
import types
import unittest
from unittest.mock import MagicMock, patch

import numpy as np
from PIL import Image

ENGINE = Path(__file__).resolve().parents[1] / 'Engine'
sys.path.insert(0, str(ENGINE))

# Replace Windows integrations before importing the engine, including on macOS.
keyboard = MagicMock()
keyboard.is_pressed.return_value = False
mouse = MagicMock()
mouse.FAILSAFE = True
mouse.position.return_value = types.SimpleNamespace(x=10, y=10)
sys.modules['keyboard'] = keyboard
sys.modules['pyautogui'] = mouse
sys.modules['winsound'] = MagicMock()
sys.modules['mss'] = MagicMock()
bridge = importlib.import_module('bridge')
core = importlib.import_module('macro_core')

class EngineTests(unittest.TestCase):
    def setUp(self):
        self.events = []
        self.emit_patch = patch.object(bridge, 'emit', lambda kind, **data: self.events.append(dict(type=kind, **data)))
        self.emit_patch.start()
        self.engine = bridge.Engine()
        mouse.reset_mock()
        keyboard.is_pressed.return_value = False

    def tearDown(self):
        self.engine.cancel.set()
        self.wait_idle()
        self.emit_patch.stop()

    def wait_idle(self):
        end = time.monotonic() + 2
        while self.engine.busy and time.monotonic() < end: time.sleep(.005)
        self.assertFalse(self.engine.busy)

    def configure(self):
        self.engine.monitor_region = (0, 0, 30, 30)
        self.engine.target_colors = [(10, 20, 30)]
        self.engine.final_click_pos = (100, 120)
        self.engine.no_refresh_mode.set(True)

    def test_countdown_stop_never_enters_scan(self):
        self.configure()
        with patch.object(self.engine, 'macro_loop') as scan:
            self.engine.start()
            self.engine.stop_macro()
            self.wait_idle()
            scan.assert_not_called()

    def test_double_start_has_one_worker(self):
        self.configure()
        started = threading.Event()
        def loop():
            started.set()
            self.engine.cancel.wait(1)
        self.engine.start_wait_time.set(0)
        with patch.object(self.engine, 'macro_loop', side_effect=loop) as scan:
            self.engine.start()
            self.assertTrue(started.wait(1))
            self.engine.start()
            self.engine.stop_macro()
            self.wait_idle()
            self.assertEqual(scan.call_count, 1)

    def test_coordinate_selection_cancel_releases_busy_state(self):
        self.engine.select('region')
        self.engine.stop_macro()
        self.wait_idle()
        self.assertIsNone(self.engine.monitor_region)
        self.assertFalse(any(e['type'] == 'warning' for e in self.events))


    def test_yes24_requires_focus(self):
        self.configure()
        self.engine.is_yes24_mode.set(True)
        self.engine.start()
        self.assertFalse(self.engine.busy)
        self.assertTrue(any(e['type'] == 'warning' and e['title'] == 'YES24 설정' for e in self.events))

    def test_cancel_suppresses_retry_clicks(self):
        self.configure()
        self.engine.cancel.set()
        self.assertFalse(self.engine._verify_and_retry(None, np.array([30,20,10]), 10,10,25,(0,0,30,30)))
        mouse.click.assert_not_called()

    def test_center_is_an_actual_pixel_in_first_component(self):
        first = [(y,x) for y in range(2,7) for x in range(3,8)]
        second = [(y,x) for y in range(2,7) for x in range(10,15)]
        matches = np.array(sorted(first+second))
        chosen = self.engine._pick_seat_point(matches)
        self.assertIn(chosen, first)
        self.assertEqual(chosen, (4,5))

    def test_refinement_preserves_component_center(self):
        shot = np.zeros((30,30,4), dtype=np.uint8)
        shot[6:15, 8:19, :3] = (30,20,10)
        sct = MagicMock()
        sct.grab.return_value = shot
        x,y,size = self.engine._refine_click_point(sct,np.array([30,20,10]),9,7,25,(0,0,30,30))
        self.assertEqual((x,y,size),(13,10,(11,9)))

    def test_both_engines_click_then_final(self):
        for method in (1,2):
            with self.subTest(method=method):
                self.configure()
                self.engine.cancel.clear()
                self.engine.AUTO_RETRY = False
                self.engine.is_running = True
                self.engine.scan_method.set(method)
                shot = np.zeros((30,30,4), dtype=np.uint8)
                shot[6:15,8:19,:3] = (30,20,10)
                sct = MagicMock()
                sct.grab.return_value = shot
                context = MagicMock()
                context.__enter__.return_value = sct
                self.engine.root = MagicMock()
                self.engine.root.after.side_effect = lambda delay, callback: callback()
                self.engine.root.winfo_screenwidth.return_value = 30
                self.engine.root.winfo_screenheight.return_value = 30
                rgb = Image.fromarray(shot[:,:,:3][:,:,::-1].copy())
                clicks = []
                with patch.object(core.mss, 'mss', return_value=context), \
                     patch.object(core.ImageGrab, 'grab', return_value=rgb), \
                     patch.object(self.engine, '_click_at', side_effect=lambda x,y,**kw: clicks.append((x,y))), \
                     patch.object(self.engine, 'play_success_sound'):
                    self.engine.macro_loop()
                self.assertEqual(len(clicks), 2)
                self.assertEqual(clicks[-1], (100,120))
                self.assertTrue(8 <= clicks[0][0] < 19 and 6 <= clicks[0][1] < 15)
                self.assertTrue(self.engine.success_pending)

    def test_refresh_modes_keep_original_priority(self):
        cases = [('default', {}, 'f5'), ('yes24', {'is_yes24_mode':True}, 'arrows'),
                 ('single', {'use_custom_refresh':True}, 'click1'),
                 ('multi', {'use_multi_refresh':True}, 'either'),
                 ('none', {'no_refresh_mode':True}, 'none')]
        for name, options, expected in cases:
            with self.subTest(name=name):
                self.configure()
                self.engine.target_colors = [(100,100,100)]
                self.engine.cancel.clear()
                self.engine.is_running = True
                self.engine.no_refresh_mode.set(False)
                self.engine.is_yes24_mode.set(False)
                self.engine.use_custom_refresh.set(False)
                self.engine.use_multi_refresh.set(False)
                self.engine.refresh_click_pos_1 = (1,2)
                self.engine.refresh_click_pos_2 = (3,4)
                for key,value in options.items(): getattr(self.engine,key).set(value)
                mouse.reset_mock()
                sct = MagicMock()
                sct.grab.return_value = np.zeros((30,30,4),dtype=np.uint8)
                context = MagicMock()
                context.__enter__.return_value = sct
                self.engine.root = MagicMock()
                self.engine.root.after.side_effect = lambda delay, callback: callback()
                self.engine.root.winfo_screenwidth.return_value = 30
                self.engine.root.winfo_screenheight.return_value = 30
                self.engine.count_label = MagicMock()
                self.engine.count_label.config.side_effect = lambda **kwargs: self.engine.cancel.set()
                with patch.object(core.mss,'mss',return_value=context): self.engine.macro_loop()
                if expected == 'f5': mouse.press.assert_called_once_with('f5')
                elif expected == 'arrows': self.assertEqual([c.args[0] for c in mouse.press.call_args_list],['up','down'])
                elif expected == 'click1': mouse.click.assert_called_once_with((1,2))
                elif expected == 'either':
                    self.assertEqual(mouse.click.call_count,1)
                    self.assertIn(mouse.click.call_args.args[0],[(1,2),(3,4)])
                else:
                    mouse.press.assert_not_called()
                    mouse.click.assert_not_called()


    def test_starts_without_configuration_or_network(self):
        self.configure()
        self.engine.start_wait_time.set(0)
        with patch.object(self.engine, 'macro_loop') as scan, \
             patch.object(socket, 'create_connection', side_effect=AssertionError('Network is forbidden')) as network:
            self.engine.start()
            self.wait_idle()
            scan.assert_called_once()
            network.assert_not_called()
        self.assertFalse(any(e['type'] == 'warning' for e in self.events))

    def test_engine_initializes_without_reading_configuration(self):
        with patch.object(Path, 'read_text', side_effect=AssertionError('No configuration file required')):
            instance = bridge.Engine()
        self.assertFalse(instance.busy)


    def test_nol_presets_replace_manual_colors_and_general_preserves_them(self):
        self.configure()
        manual = list(self.engine.target_colors)
        self.engine.apply_settings(dict(site_mode='nol', selected_presets=['purple', 'blue', 'purple']))
        self.assertEqual(self.engine.detection_colors(), [(208,202,246),(144,208,238)])
        self.assertEqual(self.engine.selected_presets, ['purple','blue'])
        self.engine.apply_settings(dict(site_mode='general'))
        self.assertEqual(self.engine.detection_colors(), manual)
        self.engine.apply_settings(dict(site_mode='nol', selected_presets=[]))
        self.assertEqual(self.engine.detection_colors(), manual)

    def test_nol_presets_allow_start_without_sampling_colors(self):
        self.configure()
        self.engine.target_colors = []
        self.engine.start_wait_time.set(0)
        self.engine.apply_settings(dict(site_mode='nol', selected_presets=['green','brown']))
        self.assertTrue(self.engine.all_setup_notified)
        with patch.object(self.engine, 'macro_loop') as scan:
            self.engine.start()
            self.wait_idle()
            scan.assert_called_once()

    def test_invalid_preset_is_rejected_without_changing_mode(self):
        for presets in (['unknown'], 'purple', [True]):
            with self.assertRaises(ValueError):
                self.engine.apply_settings(dict(site_mode='nol', selected_presets=presets))
            self.assertEqual(self.engine.site_mode, 'general')

    def test_button_patch_handles_negative_monitor_coordinates(self):
        self.engine.final_click_pos = (-200, 10)
        sct = MagicMock()
        sct.monitors = [dict(left=-1920, top=-100, width=3840, height=1180)]
        sct.grab.return_value = np.full((9,9,4),(12,34,56,255),dtype=np.uint8)
        np.testing.assert_array_equal(self.engine._grab_button_patch(sct),[12,34,56])
        self.assertEqual(sct.grab.call_args.args[0],dict(left=-204,top=6,width=9,height=9))

    def test_button_change_needs_two_consecutive_samples(self):
        base=np.array([100,100,100],dtype=float)
        changed=base+20
        reads=[changed,base,changed,changed]
        with patch.object(self.engine,'_grab_button_patch',side_effect=reads) as capture, \
             patch.object(self.engine,'_refine_click_point') as refine, \
             patch.object(self.engine,'_click_at') as click:
            self.assertTrue(self.engine._confirm_seat_click(None,base,5,5,base,25,(0,0,30,30)))
            self.assertEqual(capture.call_count,4)
            refine.assert_not_called()
            click.assert_not_called()

    def test_nol_retry_is_single_seat_click_and_has_a_limit(self):
        base=np.array([100,100,100],dtype=float)
        self.engine.RETRY_WAIT=0
        with patch.object(self.engine,'_grab_button_patch',return_value=base), \
             patch.object(self.engine,'_refine_click_point',return_value=(9,10,None)), \
             patch.object(self.engine,'_click_at') as click:
            self.assertFalse(self.engine._confirm_seat_click(None,base,5,5,base,25,(0,0,30,30)))
            self.assertEqual(click.call_count,3)
            self.assertTrue(all(call.args==(9,10) and call.kwargs==dict(clicks=1,hover=.03) for call in click.call_args_list))

    def test_nol_wait_can_be_cancelled_without_reclick(self):
        base=np.array([100,100,100],dtype=float)
        def cancel_after_capture(_):
            self.engine.cancel.set()
            return base
        with patch.object(self.engine,'_grab_button_patch',side_effect=cancel_after_capture), \
             patch.object(self.engine,'_click_at') as click:
            self.assertFalse(self.engine._confirm_seat_click(None,base,5,5,base,25,(0,0,30,30)))
            click.assert_not_called()

    def test_nol_integration_waits_before_final_for_both_engines(self):
        for method in (1,2):
            for accepted in (True,False):
                with self.subTest(method=method,accepted=accepted):
                    self.configure()
                    self.engine.site_mode='nol'
                    self.engine.AUTO_RETRY=True
                    self.engine.scan_method.set(method)
                    self.engine.is_running=True
                    self.engine.success_pending=False
                    self.engine.cancel.clear()
                    shot=np.zeros((30,30,4),dtype=np.uint8)
                    shot[6:15,8:19,:3]=(30,20,10)
                    sct=MagicMock()
                    sct.grab.return_value=shot
                    context=MagicMock()
                    context.__enter__.return_value=sct
                    self.engine.root=MagicMock()
                    self.engine.root.winfo_screenwidth.return_value=30
                    self.engine.root.winfo_screenheight.return_value=30
                    rgb=Image.fromarray(shot[:,:,:3][:,:,::-1].copy())
                    steps=[]
                    def clicked(x,y,**kwargs):
                        steps.append('final' if (x,y)==(100,120) else 'seat')
                    def confirmed(*args):
                        steps.append('confirm')
                        return accepted
                    with patch.object(core.mss,'mss',return_value=context), \
                         patch.object(core.ImageGrab,'grab',return_value=rgb), \
                         patch.object(self.engine,'_grab_button_patch',return_value=np.array([0,0,0])), \
                         patch.object(self.engine,'_confirm_seat_click',side_effect=confirmed), \
                         patch.object(self.engine,'_verify_and_retry') as general_retry, \
                         patch.object(self.engine,'_click_at',side_effect=clicked), \
                         patch.object(self.engine,'play_success_sound'):
                        self.engine.macro_loop()
                    self.assertEqual(steps,['seat','confirm','final'] if accepted else ['seat','confirm'])
                    self.assertEqual(self.engine.success_pending,accepted)
                    general_retry.assert_not_called()

if __name__ == '__main__': unittest.main()

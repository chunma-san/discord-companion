import importlib.util,pathlib,unittest
spec=importlib.util.spec_from_file_location('broker',pathlib.Path(__file__).with_name('host_broker.py'))
broker=importlib.util.module_from_spec(spec);spec.loader.exec_module(broker)
class BrokerTests(unittest.TestCase):
    def test_rejects_arbitrary_commands(self):
        for request in [{'action':'shell','service':'id'},{'action':'service','service':'ssh'},
                        {'action':'service','service':'Docker; reboot'},{'action':'server','command':'id'},
                        {'action':'teamspeak-start','service':'Docker'},[],{'action':[]}]:
            with self.assertRaises((ValueError,TypeError)):broker.validate(request)
    def test_status_allowlist(self):
        for service in broker.SERVICES:self.assertEqual(broker.validate({'action':'service','service':service}),('service',service))
    def test_timer_writes_exact_fixed_command(self):
        from unittest.mock import patch
        import types,stat
        for action,expected in [('teamspeak-start',b'start\n'),('teamspeak-stop',b'stop\n')]:
            with patch.object(broker.pathlib.Path,'exists',return_value=False),patch.object(broker.os,'stat',return_value=types.SimpleNamespace(st_mode=stat.S_IFIFO)),patch.object(broker.os,'open',return_value=9),patch.object(broker.os,'write') as write,patch.object(broker.os,'close'):
                self.assertEqual(broker.handle({'action':action})['TeamSpeak bots'],'unchanged');write.assert_called_once_with(9,expected)
    def test_maintenance_blocks_timer(self):
        from unittest.mock import patch
        with patch.object(broker.pathlib.Path,'exists',return_value=True),patch.object(broker.os,'open') as opened:
            with self.assertRaises(RuntimeError):broker.handle({'action':'teamspeak-start'})
            opened.assert_not_called()
if __name__=='__main__':unittest.main()

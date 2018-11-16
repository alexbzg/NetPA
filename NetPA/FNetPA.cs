using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using Jerome;
using System.IO;
using System.Xml.Serialization;
using System.Threading.Tasks;
using System.Net;
using AsyncConnectionNS;
using JeromeModuleSettings;
using System.Threading;
using StorableFormState;
using NetComm;
using ExpertSync;

namespace NetPA
{
    public partial class FNetPA : FormWStorableState<NetCommConfig>
    {
        public static NetPAControllerTemplate controllerTemplate = new NetPAControllerTemplate
        {
            limits = new Dictionary<int, int> { { -1, 22 }, { 1, 21 } },
            enable = 2,
            pulse = 4,
            dir = 6,
            ptt = 1,
            relays = new int[] { 3, 5 }
        };
        public static readonly int[] buttonPositions = new int[] { 0, 275, 500, 700, 950, 1175, 1400};

        protected Dictionary<JeromeConnectionParams, JeromeConnectionState> connections = new Dictionary<JeromeConnectionParams,JeromeConnectionState>();
        protected List<CheckBox> buttons = new List<CheckBox>();
        protected List<CheckBox> buttonsRelay = new List<CheckBox>();
        protected List<string> buttonLabels = new List<string>();
        protected List<string> buttonRelayLabels = new List<string>();
        protected Dictionary<JeromeConnectionParams, ToolStripMenuItem> menuControl = new Dictionary<JeromeConnectionParams, ToolStripMenuItem>();
        protected Color buttonsColor;
        protected Dictionary<int, int> esBindings = new Dictionary< int, int> ();
        protected bool trx = false;
        protected volatile bool closing = false;
        protected JeromeConnectionState activeConnection;
        protected volatile int _position = -1;
        protected int position
        {
            get { return _position; }
            set
            {
                _position = value;
                if (_position > buttonPositions[buttonPositions.Length - 1])
                    _position = buttonPositions[buttonPositions.Length - 1];
                checkLimitPosition();
                foreach (int c in new int[] { 0, buttonPositions.Length - 1 } )
                    if (_position == buttonPositions[c])
                    {
                        Invoke((MethodInvoker) delegate { buttons[c].Checked = true; });
                        break;
                    }
            }
        }
        protected int _limit = 0;
        protected int limit
        {
            get { return _limit; }
            set
            {
                _limit = value;
                checkLimitPosition();
            }
        }
        private CancellationTokenSource rotateTaskCTS = new CancellationTokenSource();
        private volatile Task rotateTask;
        private System.Threading.Timer blinkTimer;
        protected volatile int target = -1;

        private ExpertSyncConnector esConnector;

        private void checkLimitPosition()
        {
            if (limit == -1)
                _position = 0;
            if (limit == 1)
                _position = buttonPositions[buttonPositions.Length - 1];
            Invoke((MethodInvoker)delegate { slPosition.Text = _position.ToString(); });

        }

        private void onBlinkTimer(object state)
        {
            Invoke((MethodInvoker)delegate { lRotation.Visible = !lRotation.Visible; });
        }

        private bool connected
        {
            get
            {
                return connections.Values.ToList().Exists( x => x.active && x.connected );
            }
        }

        protected void updateButtonLabel(int no)
        {
            if (buttonLabels[no].Equals(string.Empty))
                buttons[no].Text = (no + 1).ToString();
            else
                buttons[no].Text = buttonLabels[no];
        }

        protected void updateButtonRelayLabel(int no)
        {
            if (buttonRelayLabels[no].Equals(string.Empty))
                buttonsRelay[no].Text = (no + 1).ToString();
            else
                buttonsRelay[no].Text = buttonRelayLabels[no];
        }


        public FNetPA()
        {
            InitializeComponent();
            Width = 200;

            config.data.initialize();
            if (config.data.connections != null)
            {
                for (int co = 0; co < config.data.connections.Count(); co++)
                {
                    connections[config.data.connections[co]] = config.data.states[co];
                }
            }
            else
                connections = new Dictionary<JeromeConnectionParams, JeromeConnectionState>();
            if (config.data.buttonLabels != null)
                buttonLabels = config.data.buttonLabels.ToList();
            if (config.data.buttonRelayLabels != null)
                buttonRelayLabels = config.data.buttonRelayLabels.ToList();
            if (config.data.esMhzValues != null)
                for (int co = 0; co < config.data.esButtons.Count(); co++)
                    esBindings[config.data.esMhzValues[co]] = config.data.esButtons[co];

            for (int co = buttonLabels.Count(); co < buttonPositions.Count(); co++)
                buttonLabels.Add("");
            for (int co = buttonRelayLabels.Count(); co < controllerTemplate.relays.Length; co++)
                buttonRelayLabels.Add("");
            foreach ( JeromeConnectionParams c in connections.Keys )
                createConnectionMI( c );
            Parallel.ForEach(connections.Where(x => x.Value.active),
                x => connect(x.Key));
            blinkTimer = new System.Threading.Timer(onBlinkTimer, null, Timeout.Infinite, Timeout.Infinite);
        }


        protected void createConnectionMI(JeromeConnectionParams c)
        {
            ToolStripMenuItem mi = new ToolStripMenuItem();
            mi.Text = c.name;
            mi.Visible = !connected;
            mi.Click += delegate(object sender, EventArgs e)
            {
                if (connections[c].active)
                    disconnect(c);
                else
                    connect(c);
            };
            miControl.DropDownItems.Add(mi);
            menuControl[c] = mi;
        }

        protected void disconnect(JeromeConnectionParams c)
        {
            if (connections[c].controller != null)
                connections[c].controller.disconnect();
            connections[c].active = false;
            writeConfig();
        }

        protected void connect(JeromeConnectionParams cp)
        {
            foreach (JeromeConnectionParams c in connections.Keys)
            {
                if (connections[c].active)
                {
                    disconnect(c);
                    menuControl[c].Checked = false;
                }
            }
            connections[cp].active = true;
            connections[cp].controller = JeromeController.create(cp);
            connections[cp].controller.onDisconnected += controllerDisconnected;
            connections[cp].controller.onConnected += controllerConnected;
            writeConfig();
            connections[cp].controller.asyncConnect();
            activeConnection = connections[cp];
        }

        protected void controllerConnected( object sender, EventArgs e )
        {
            bool senderFound = false;
            KeyValuePair<JeromeConnectionParams, JeromeConnectionState> senderEntry =
                connections.FirstOrDefault(x => x.Value.controller == sender && (senderFound = true));
            if (!senderFound)
                return;
            JeromeConnectionParams cp = senderEntry.Key;
            this.Invoke((MethodInvoker)delegate
          {
              connections[cp].watch = false;
              menuControl[cp].Checked = true;
              this.Text = cp.name;
              updateButtonsMode();
          });
            JeromeController controller = connections[cp].controller;
            string linesState = controller.readlines();
            controller.setLineMode(controllerTemplate.dir, 0);
            controller.setLineMode(controllerTemplate.pulse, 0);
            controller.setLineMode(controllerTemplate.enable, 0);
            controller.setLineMode(controllerTemplate.ptt, 0);
            controller.switchLine(controllerTemplate.enable, 1);
            controller.switchLine(controllerTemplate.pulse, 0);
            controller.switchLine(controllerTemplate.ptt, 0);
            for (int co = 0; co < controllerTemplate.relays.Length; co++)
            {
                controller.setLineMode(controllerTemplate.relays[co], 0);
                controller.switchLine(controllerTemplate.relays[co], 0);
            }
            foreach (int line in controllerTemplate.limits.Values)
                controller.setLineMode(line, 1);
            controller.lineStateChanged += controllerLineStateChanged;
            if (linesState[controllerTemplate.limits[-1] - 1] == '0')
                limit = -1;
            else if (linesState[controllerTemplate.limits[1] - 1] == '0')
                limit = 1;
            if (position == -1)
                rotate(0);
        }

        private void controllerLineStateChanged(object sender, LineStateChangedEventArgs e)
        {
            System.Diagnostics.Debug.WriteLine(e.line.ToString() + " " + e.state.ToString());
            if ( controllerTemplate.limits.ContainsValue(e.line))
            {
                if (e.state == 0)
                {
                    System.Diagnostics.Debug.WriteLine("Limit");
                    if (e.line == controllerTemplate.limits[-1])
                        limit = -1;
                    else
                        limit = 1;
                    if (target == -1 || target == position)
                    {
                        activeConnection.controller.switchLine(controllerTemplate.enable, 1);
                        activeConnection.controller.switchLine(controllerTemplate.pulse, 0);
                    }
                    else
                        rotate(target);
                }
                else
                {
                    limit = 0;
                }

            }
        }

        private void clearRotateTask()
        {
            rotateTask = null;
            rotateTaskCTS.Dispose();
            rotateTaskCTS = new CancellationTokenSource();
        }

        private void rotate( int newTarget)
        {
            System.Diagnostics.Debug.WriteLine("Rotate to " + newTarget.ToString());
            target = newTarget;
            if (activeConnection.controller == null || !activeConnection.controller.connected || position == newTarget)
                return;
            if (rotateTask == null)
                rotateTask = Task.Run(async () =>
               {
                   JeromeController controller = activeConnection.controller;
                   System.Diagnostics.Debug.WriteLine("start rotate to " + target.ToString());
                   blinkTimer.Change(1000, 1000);
                   controller.switchLine(controllerTemplate.enable, 0);
                   controller.switchLine(controllerTemplate.ptt, 1);
                   while (target != position && controller.connected)
                   {
                       int dir;
                       if (position == -1)
                           dir = target == buttonPositions[buttonPositions.Length - 1] ? 1 : -1;
                       else
                           dir = target < position ? -1 : 1;
                       controller.switchLine(controllerTemplate.dir, dir == -1 ? 0 : 1);
                       controller.switchLine(controllerTemplate.pulse, 1);
                       await Task.Delay(5);
                       controller.switchLine(controllerTemplate.pulse, 0);
                       await Task.Delay(5);
                       if (position != -1 )
                           position += dir;
                       /*if (position == 0 || position == buttonPositions[buttonPositions.Count() - 1])
                       {
                           System.Diagnostics.Debug.WriteLine("Limit reached " + position.ToString());
                           break;
                       }*/
                   }
                   System.Diagnostics.Debug.WriteLine("Rotated to " + position.ToString());
                   controller.switchLine(controllerTemplate.pulse, 0);
                   controller.switchLine(controllerTemplate.enable, 1);
                   controller.switchLine(controllerTemplate.ptt, 0);
                   blinkTimer.Change(Timeout.Infinite, Timeout.Infinite);
                   clearRotateTask();
                   Invoke((MethodInvoker)delegate { lRotation.Visible = false; });
                   if (position != target)
                       rotate(target);
               });
        }

        protected void updateButtonsMode()
        {
            for (int co = 0; co < buttons.Count(); co++)
                buttons[co].Enabled = !trx && connected;
            foreach (CheckBox b in buttonsRelay)
                b.Enabled = !trx && connected;
        }

        protected void controllerDisconnected(object obj, DisconnectEventArgs e)
        {
            JeromeConnectionParams c = ((JeromeController)obj).connectionParams;
            this.Invoke((MethodInvoker)delegate
            {
                if (e.requested)
                {
                    menuControl[c].Checked = false;
                    this.Text = "Ant Comm";
                }
                else
                {
                    this.Text = "Disconnected!";
                }
                updateButtonsMode();
            });
        }

        private void esItem_Click(object sender, EventArgs e)
        {
            FESConnection fesc = new FESConnection(config.data.esHost, config.data.esPort);
            if (fesc.ShowDialog() == DialogResult.OK &&
                (config.data.esHost != fesc.host || config.data.esPort != fesc.port))
            {
                config.data.esHost = fesc.host;
                config.data.esPort = fesc.port;
                esConnect();
            }
        }


        private void esConnect()
        {
#if DEBUG
#if DISABLE_ES
                    return;
#endif
#endif
            if (esConnector != null && esConnector.connected)
                esConnector.disconnect();
            if (config.data.esHost != null && config.data.esPort != 0)
            {
                writeConfig();
                esConnector = new ExpertSyncConnector(config.data.esHost, config.data.esPort);
                esConnector.reconnect = true;
                esConnector.onMessage += esMessage;
                esConnector.asyncConnect();
            }
        }

        protected void FMain_Load(object sender, EventArgs e)
        {
            Width = 100;
            for (int co = 0; co < buttonPositions.Count(); co++)
            {
                CheckBox b = new CheckBox();
                b.Height = 25;
                b.Width = Width - 7;
                b.Top = 25 + (b.Height + 2) * co;
                b.Left = 1;
                b.TextAlign = ContentAlignment.MiddleCenter;
                buttons.Add(b);
                int no = co;
                updateButtonLabel(no);
                b.BackColor = SystemColors.Control;
                b.Appearance = Appearance.Button;
                b.Enabled = false;
                b.Anchor = AnchorStyles.Right | AnchorStyles.Left;
                b.CheckedChanged += new EventHandler(delegate (object obj, EventArgs ea)
                {
                    if (b.Checked)
                    {
                        buttons.Where(x => x != b).ToList().ForEach(x => x.Checked = false);
                        b.ForeColor = Color.Red;
                        Task.Run(() => { rotate(buttonPositions[no]); });
                    }
                    else
                    {
                        b.ForeColor = buttonsColor;
                    }
                });
                b.MouseDown += form_MouseClick;
                Controls.Add(b);
            }
            buttonsColor = buttons[0].ForeColor;
            for (int co = 0; co < controllerTemplate.relays.Length; co++)
            {
                CheckBox b = new CheckBox();
                b.Height = 25;
                b.Width = Width - 7;
                b.Top = 50 + 27 * buttonPositions.Count() + (b.Height + 2) * co;
                b.Left = 1;
                b.TextAlign = ContentAlignment.MiddleCenter;
                int no = co;
                buttonsRelay.Add(b);
                updateButtonRelayLabel(no);
                b.BackColor = SystemColors.Control;
                b.Appearance = Appearance.Button;
                b.Enabled = false;
                b.Anchor = AnchorStyles.Right | AnchorStyles.Left;
                b.MouseDown += new MouseEventHandler(delegate (object obj, MouseEventArgs ea)
                {
                    if (ea.Button == MouseButtons.Left && activeConnection != null && activeConnection.connected)
                        activeConnection.controller.switchLine(controllerTemplate.relays[no], 1);
                });
                b.MouseDown += form_MouseClick;
                b.MouseUp += new MouseEventHandler(delegate (object obj, MouseEventArgs ea)
                {
                    if (ea.Button == MouseButtons.Left && activeConnection != null && activeConnection.connected)
                        activeConnection.controller.switchLine(controllerTemplate.relays[no], 0);
                });
                Controls.Add(b);
            }
        }


        public override void writeConfig()
        {
            if (!loaded)
                return;
            config.data.lastConnection = -1;

            config.data.connections = new JeromeConnectionParams[connections.Count];
            config.data.states = new JeromeConnectionState[connections.Count];
            int co = 0;
            foreach (KeyValuePair<JeromeConnectionParams, JeromeConnectionState> x in connections)
            {
                if (x.Value.active)
                    config.data.lastConnection = co;
                config.data.connections[co] = x.Key;
                config.data.states[co] = x.Value;
                co++;
            }

            config.data.buttonLabels = buttonLabels.ToArray();
            config.data.buttonRelayLabels = buttonRelayLabels.ToArray();

             config.data.esMhzValues = new int[ esBindings.Count ];
             config.data.esButtons = new int[esBindings.Count];
             co = 0;
             foreach ( KeyValuePair<int,int> x in esBindings ) {
                 config.data.esMhzValues[co] = x.Key;
                 config.data.esButtons[co] = x.Value;
                 co++;
             }

            config.write();
        }

        protected void initConfig()
        {
        }

        protected void miModuleSettings_Click(object sender, EventArgs e)
        {
            (new fModuleSettings()).ShowDialog();
        }

        protected void miConnectionsList_Click(object sender, EventArgs e)
        {
            FConnectionsList flist = new FConnectionsList(connections.Keys.ToList());
            flist.connectionCreated += connectionCreated;
            flist.connectionEdited += connectionEdited;
            flist.connectionDeleted += connectionDeleted;
            flist.ShowDialog(this);
        }

        protected void connectionCreated(object obj, EventArgs e)
        {
            JeromeConnectionParams c = (JeromeConnectionParams)obj;
            connections[c] = new JeromeConnectionState();
            createConnectionMI(c);
            writeConfig();
        }

        protected void connectionEdited(object obj, EventArgs e)
        {
            JeromeConnectionParams c = (JeromeConnectionParams)obj;
            menuControl[c].Text = c.name;
            c.usartPort = 0;
            writeConfig();
        }

        protected void connectionDeleted(object obj, EventArgs e)
        {
            JeromeConnectionParams c = (JeromeConnectionParams)obj;
            if (connections[c].active)
                MessageBox.Show("Нельзя удалить активное соединение! Изменения не будут сохранены");
            else
            {
                connections.Remove(c);
                miControl.DropDownItems.Remove(menuControl[c]);
                menuControl.Remove(c);
                writeConfig();
            }
        }


        protected void form_MouseClick(object sender, MouseEventArgs e)
        {
            //System.Diagnostics.Debug.WriteLine("Mouse click");
            if (e.Button == MouseButtons.Right)
            {
                CheckBox b = null;
                if (sender.GetType() == typeof(CheckBox))
                    b = (CheckBox)sender;
                else
                {
                    Control i = GetChildAtPoint(new Point(e.X, e.Y));
                    if (i != null && i.GetType() == typeof(CheckBox))
                        b = (CheckBox)i;
                }
                if ( b != null && buttons.Contains(b))
                {
                    int no = buttons.IndexOf(b);
                    string bindStr = string.Join("; ", esBindings.Where(x => x.Value == no).Select(x => x.Key).ToArray());
                    FButtonProps ib = new FButtonProps(buttonLabels[no], bindStr);
                    ib.StartPosition = FormStartPosition.CenterParent;
                    ib.ShowDialog(this);
                    if (ib.DialogResult == DialogResult.OK)
                    {
                        buttonLabels[no] = ib.name;
                        b.Text = ib.name;
                        foreach (KeyValuePair<int, int> x in esBindings.Where(x => x.Value == no).ToList())
                            esBindings.Remove(x.Key);
                        foreach (int mhz in ib.esMHzValues)
                            esBindings[mhz] = no;
                        writeConfig();
                        updateButtonLabel(no);
                    }
                }
                if (b != null && buttonsRelay.Contains(b))
                {
                    int no = buttonsRelay.IndexOf(b);
                    FButtonProps ib = new FButtonProps(buttonRelayLabels[no]);
                    ib.StartPosition = FormStartPosition.CenterParent;
                    ib.ShowDialog(this);
                    if (ib.DialogResult == DialogResult.OK)
                    {
                        buttonRelayLabels[no] = ib.name;
                        b.Text = ib.name;
                        writeConfig();
                        updateButtonRelayLabel(no);
                    }
                }
            }
        }


        public void esMessage(object Sender, MessageEventArgs e)
        {
            if (trx != e.trx)
            {
                trx = e.trx;
                this.Invoke((MethodInvoker)delegate {
                    updateButtonsMode();
                });
            }
            int mhz = ((int)e.vfoa) / 1000000;
            if ( esBindings.ContainsKey( mhz ) ) 
                Invoke( (MethodInvoker) delegate {
                    buttons[esBindings[mhz]].Checked = true;
                });
        }


        protected void FMain_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9 && e.KeyCode - Keys.NumPad0 - 1 < buttons.Count)
            {
                CheckBox b = buttons[e.KeyCode - Keys.NumPad0 - 1];
                if (b.Enabled)
                    b.Checked = !b.Checked;
               /* else
                    MessageBox.Show((e.KeyCode - Keys.NumPad0 - 1).ToString());*/
            }
        }

        protected void FMain_ResizeEnd(object sender, EventArgs e)
        {
            if (buttons.Count() + buttonsRelay.Count() > 0)
            {
                int bHeight = (this.ClientSize.Height - 75) / (buttons.Count() + buttonsRelay.Count()) - 2;
                for (int co = 0; co < buttons.Count(); co++)
                {
                    CheckBox b = buttons[co];
                    b.Height = bHeight;
                    b.Top = 25 + (bHeight + 2) * co;
                }
                for (int co = 0; co < buttonsRelay.Count(); co++)
                {
                    CheckBox b = buttonsRelay[co];
                    b.Height = bHeight;
                    b.Top = 50 + (bHeight + 2) * (buttons.Count() + co);
                }
            }
        }

        private async Task disconnectTask(JeromeConnectionState st)
        {
            st.controller.onDisconnected -= controllerDisconnected;
            st.controller.onConnected -= controllerConnected;
            st.controller.disconnect();
            st.controller = null;
        }

        protected async void FNetPA_FormClosed(object sender, FormClosedEventArgs e)
        {
            closing = true;
            await Task.WhenAll(connections.Where(x => x.Value.controller != null)
                .Select(x => disconnectTask(x.Value)));
        }

    }

    public class NetPAControllerTemplate
    {
        public Dictionary<int, int> limits;
        public int enable;
        public int pulse;
        public int dir;
        public int ptt;
        public int[] relays;
    }
        


}

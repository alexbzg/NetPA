using Jerome;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Xml.Serialization;
using StorableFormState;
using System.Runtime.Serialization;

namespace NetComm
{
    public class JeromeConnectionState
    {
        public bool watch = false;
        public bool active = false;
        public bool[] linesStates;
        [XmlIgnoreAttribute]
        public JeromeController controller = null;
        public int[] lines;

        public JeromeConnectionState()
        {
            linesStates = new bool[22];
            lines = new int[22];
            for (int co = 0; co < linesStates.Count(); co++)
            {
                linesStates[co] = false;
                lines[co] = co + 1;
            }

        }

        public bool connected
        {
            get
            {
                return controller != null && controller.connected;
            }
        }
    }

    [DataContract]
    public class NetCommConfig : StorableFormConfig
    {
        [DataMember]
        public JeromeConnectionParams[] connections;
        [DataMember]
        public JeromeConnectionState[] states;
        [DataMember]
        public string[] buttonLabels;
        [DataMember]
        public string[] buttonRelayLabels;
        [DataMember]
        public int[] esMhzValues;
        [DataMember]
        public int[] esButtons;
        [DataMember]
        public int lastConnection;
        [DataMember]
        public string esHost = "127.0.0.1";
        [DataMember]
        public int esPort = 50040;


        public void initialize()
        {
            if (connections != null)
            {
                if (states == null || states.Count() == 0)
                    states = new JeromeConnectionState[connections.Count()];
                for (int co = 0; co < connections.Count(); co++)
                {
                    if (states[co] == null)
                        states[co] = new JeromeConnectionState();
                }
            }
        }


    }
}

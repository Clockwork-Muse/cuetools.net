﻿using System;
using System.Collections.Generic;
using System.Text;
using System.ComponentModel;

namespace CUETools.Codecs
{
    public class UserDefinedEncoderSettings : AudioEncoderSettings
    {
        public UserDefinedEncoderSettings()
            : base()
        {
        }

        public string SupportedModes
        {
            get
            {
                return m_supported_modes;
            }
            set
            {
                m_supported_modes = value;
            }
        }

        public string DefaultMode
        {
            get
            {
                return m_default_mode;
            }
            set
            {
                m_default_mode = value;
            }
        }
    }
}

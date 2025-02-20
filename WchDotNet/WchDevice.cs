﻿using LibUsbDotNet;
using LibUsbDotNet.Main;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WchDotNet.Devices;

namespace WchDotNet
{
    public class WchDevice : IDisposable
    {
        private UsbDevice UsbDevice;
        private UsbEndpointReader UsbReader;
        private UsbEndpointWriter UsbWriter;

        public Chip Chip { get; private set; }
        public byte[] ChipUid { get; private set; }
        public byte[] BootloaderVersion { get; private set; }
        public bool CodeFlashProtected { get; private set; }

        public WchDevice(UsbRegistry usbRegistry)
        {

            if (!usbRegistry.Open(out UsbDevice))
            {
                throw new Exception("Cannot open device");
            }

            IUsbDevice wholeUsbDevice = UsbDevice as IUsbDevice;
            if (!ReferenceEquals(wholeUsbDevice, null))
            {
                // This is a "whole" USB device. Before it can be used, 
                // the desired configuration and interface must be selected.

                // Select config #1
                wholeUsbDevice.SetConfiguration(1);

                // Claim interface #0.
                wholeUsbDevice.ClaimInterface(0);
            }

            // open read endpoint 2.
            UsbReader = UsbDevice.OpenEndpointReader(ReadEndpointID.Ep02);
            // open write endpoint 2.
            UsbWriter = UsbDevice.OpenEndpointWriter(WriteEndpointID.Ep02);

            // Read Chip info
            byte[] buffer = ReadConfig();
            Chip = GetChip();
            CodeFlashProtected = Chip.support_code_flash_protect && buffer[2] != 0xa5;
            BootloaderVersion = buffer[14..18];
            ChipUid = buffer[18..];
        }

        public void Dispose()
        {
            if (UsbReader != null && !UsbReader.IsDisposed)
                UsbReader.Dispose();
            UsbReader = null;
            if (UsbWriter != null && !UsbWriter.IsDisposed)
                UsbWriter.Dispose();
            UsbWriter = null;
            if (UsbDevice != null && UsbDevice.IsOpen)
                UsbDevice.Close();
            UsbDevice = null;
        }

        public Response Transfer(byte[] write_buf)
        {
            Console.WriteLine($"Transport: => {string.Join("", write_buf.Select(b => b.ToString("x2")))}");
            if (UsbWriter.Write(write_buf, 1000, out _) != ErrorCode.None)
                goto Failed;

            byte[] read_buf = new byte[1024];
            int buf_len;
            if (UsbReader.Read(read_buf, 1000, out buf_len) != ErrorCode.None)
                goto Failed;
            Console.WriteLine($"Transport: <= {string.Join("", read_buf[..buf_len].Select(b => b.ToString("x2")))}");
            return Response.FromRaw(read_buf, buf_len);
        Failed:
            throw new Exception(UsbDevice.LastErrorString);
        }

        public Chip GetChip()
        {
            byte[] buffer = Command.Identify(0, 0);
            Response response = Transfer(buffer);

            if (!response.IsOK)
                throw new Exception("Failed to idenfity chip");

            return ChipDB.FindChip(response.Payload[0], response.Payload[1]);
        }

        public byte[] ReadConfig()
        {
            byte[] buf = Command.ReadConfig(Constants.CFG_MASK_ALL);
            Response resp = Transfer(buf);

            if (!resp.IsOK)
                throw new Exception("Failed to read config from chip");

            return resp.Payload;
        }

        public bool FlashChunk(UInt32 start_address, byte[] raw, byte[] key)
        {
            var xored = raw.Select((x,i) => (byte)(x ^ key[i % 8])).ToArray();

            byte[] buf = Command.Program(start_address, 0, xored);
            var resp = Transfer(buf);
            if (!resp.IsOK)
                throw new Exception($"program 0x{start_address:x08} failed");

            return resp.IsOK;
        }
        public bool VerifyChunk(UInt32 start_address, byte[] raw, byte[] key)
        {
            var xored = raw.Select((x, i) => (byte)(x ^ key[i % 8])).ToArray();

            byte[] buf = Command.Verify(start_address, 0, xored);
            var resp = Transfer(buf);
            if (!resp.IsOK)
                throw new Exception($"verify response failed");

            if (resp.Payload[0] != 0x00)
                throw new Exception("Verify failed, mismatch");

            return true;
        }
        public bool EraseCode(UInt32 sectors)
        {
            var min_sectors = Chip.min_erase_sector_number;
            if(sectors < min_sectors)
                sectors = min_sectors;

            byte[] buf = Command.Erase(sectors);
            var resp = Transfer(buf);

            if (!resp.IsOK)
                throw new Exception($"erase failed");

            return true;
        }
        public bool EraseData(UInt16 sectors)
        {
            if (Chip.eeprom_size == 0)
                throw new Exception("chip doesn't support data EEPROM");

            throw new NotImplementedException();
        }

        /// <summary>
        /// Program the code flash.
        /// unprotect -> erase -> flash -> verify -> reset
        /// </summary>
        /// <param name="raw"></param>
        public void Flash(byte[] raw)
        {
            byte[] key = GetXorKey();
            byte key_cheksum = key.OverflowingSum();

            byte[] buf = Command.IspKey(new byte[0x1e]);
            Response resp = Transfer(buf);
            if (!resp.IsOK)
                throw new Exception("isp_key failed");
            if (resp.Payload[0] != key_cheksum)
                throw new Exception("isp_key checksum failed");

            var chunk_size = 56;
            UInt32 address = 0x0;

            foreach(var chunk in raw.Chunk(chunk_size))
            {
                FlashChunk(address, chunk.ToArray(), key);
                address += (uint)chunk.Count();
            }
            FlashChunk(address, new byte[0], key);

            Console.WriteLine($"Code flash {address} bytes written");
        }
        public void Verify(byte[] raw)
        {
            byte[] key = GetXorKey();
            byte key_cheksum = key.OverflowingSum();

            byte[] buf = Command.IspKey(new byte[0x1e]);
            Response resp = Transfer(buf);
            if (!resp.IsOK)
                throw new Exception("isp_key failed");
            if (resp.Payload[0] != key_cheksum)
                throw new Exception("isp_key checksum failed");

            var chunk_size = 56;
            UInt32 address = 0x0;

            foreach (var chunk in raw.Chunk(chunk_size))
            {
                VerifyChunk(address, chunk.ToArray(), key);
                address += (uint)chunk.Count();
            }
        }

        public void UnProtect(bool force=false)
        {
            if (!force && CodeFlashProtected)
                return;

            var buf = Command.ReadConfig(Constants.CFG_MASK_RDPR_USER_DATA_WPR);
            var resp = Transfer(buf);
            if (!resp.IsOK)
                throw new Exception("read_config failed");

            var config = resp.Payload[2..14]; // 4 x u32
            config[0] = 0xa5;
            config[1] = 0x5a;

            config[8] = 0xff;
            config[9] = 0xff;
            config[10] = 0xff;
            config[11] = 0xff;

            buf = Command.WriteConfig(Constants.CFG_MASK_RDPR_USER_DATA_WPR, config);
            resp = Transfer(buf);
            if (!resp.IsOK)
                throw new Exception("write_config failed");
        }

        public void Reset()
        {
            var buf = Command.IspEnd(1);
            var resp = Transfer(buf);
            if (!resp.IsOK)
                throw new Exception("isp_end failed");
        }

        public byte[] GetXorKey()
        {
            byte checksum = ChipUid.OverflowingSum();

            byte[] key = new byte[8] {checksum, checksum, checksum, checksum,
                                      checksum, checksum, checksum,
                                      (byte)((checksum + Chip.chip_id) & 0xff)};

            return key;
        }

        public bool CheckChipName(string name)
        {
            return Chip.name.StartsWith(name);
        }

        public string DumpConfig()
        {
            string newline = Environment.NewLine;
            string result = string.Empty;

            byte[] buffer = Command.ReadConfig(Constants.CFG_MASK_RDPR_USER_DATA_WPR);
            Response resp = Transfer(buffer);

            if (!resp.IsOK)
                return "read_config failed";

            byte[] raw = resp.Payload[2..];

            foreach(var reg_def in Chip.config_registers)
            {
                UInt32 n = BitConverter.ToUInt32(raw, reg_def.offset);
                result += $"{reg_def.name}: 0x{n:X08}" + newline;

                if (reg_def.explaination != null)
                {
                    foreach (var kv in reg_def.explaination)
                    {
                        string val = kv.Key;
                        string expain = kv.Value;
                        if (val == "_" || n == (UInt32)DeviceYamlConverter.DeserializeIntegerHelper(TypeCode.UInt32, val))
                        {
                            result += $"  `- {expain}" + newline;
                            break;
                        }
                    }
                }
                
                if(reg_def.fields != null)
                {
                    foreach (var field_def in reg_def?.fields)
                    {
                        var start = field_def.bit_range[0];
                        var end = field_def.bit_range[1];

                        var bit_width = (UInt32)(start - end) + 1;
                        var b = (n >> end) & ((UInt32)Math.Pow(2, bit_width) - 1);

                        result += $"  [{start}:{end}] {field_def.name} 0b{Convert.ToString(b,2)} (0x{b:X})" + newline;

                        if(field_def.explaination != null)
                        {
                            foreach (var kv2 in field_def.explaination)
                            {
                                if (kv2.Key == "_" || b == (UInt32)DeviceYamlConverter.DeserializeIntegerHelper(TypeCode.UInt32, kv2.Key))
                                {
                                    result += $"    `- {kv2.Value}" + newline;
                                    break;
                                }
                            }
                        }
                        
                    }
                }

            }

            return result;
        }

        public string DumpInfo()
        {
            string newline = Environment.NewLine;
            string result = string.Empty;

            if (Chip.eeprom_size > 0)
                result += $"Chip: {Chip} (Code Flash: {Chip.flash_size / 1024}KiB, Data EEPROM: {Chip.eeprom_size / 1024}KiB)" + newline;
            else
                result += $"Chip: {Chip} (Code Flash: {Chip.flash_size / 1024}KiB)\n" + newline;

            result += $"Chip UID: {string.Join("-", ChipUid.Select(b => b.ToString("x2")))}" + newline;

            result += $"BTVER(bootloader ver): " +
                $"{BootloaderVersion[0]:x}{BootloaderVersion[1]:x}" +
                $".{BootloaderVersion[2]:x}{BootloaderVersion[3]:x}" + newline;

            result += $"Code Flash protected: {CodeFlashProtected}" + newline;

            return result + DumpConfig();
        }

    }
}

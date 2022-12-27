﻿using NUnit.Framework;
using OSDP.Net.Utilities.Binary;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace OSDP.Net.Tests.Utilities
{
    [TestFixture]
    internal class BinaryUtilsTest
    {
        [Test]
        public void HexToBytes()
        {
            var actual = BinaryUtils.HexToBytes("0123456789ab").ToArray();
            Assert.That(actual, Is.EquivalentTo(new byte[] { 0x01, 0x23, 0x45, 0x67, 0x89, 0xab }));
        }

        [Test]
        public void HexStringToBitArray()
        {
            var actual = BinaryUtils.HexStringToBitArray("0123");
            Assert.That(BinaryUtils.BitArrayToString(actual), Is.EqualTo("0000000100100011"));
        }
    }
}

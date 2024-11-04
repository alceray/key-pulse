﻿// <auto-generated />
using KeyPulse.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

#nullable disable

namespace KeyPulse.Migrations
{
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20241103081617_CreateDevices")]
    partial class CreateDevices
    {
        /// <inheritdoc />
        protected override void BuildTargetModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "8.0.10");

            modelBuilder.Entity("KeyPulse.Models.USBDeviceInfo", b =>
                {
                    b.Property<string>("DeviceID")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("DeviceType")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PID")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("VID")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("DeviceID");

                    b.ToTable("Devices", (string)null);
                });
#pragma warning restore 612, 618
        }
    }
}
